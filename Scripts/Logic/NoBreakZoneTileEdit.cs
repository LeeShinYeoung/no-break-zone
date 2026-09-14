// Which tilemap edits a switched-on pylon refuses. Deliberately free of any game or Unity type so it
// can be unit tested without loading the game assemblies (Editor/LogicTests~).
//
// WHY THIS EXISTS AT ALL. Protecting a tile through its health only covers damage. An explosion also
// edits the tilemap directly: every bare `ground` tile in the blast is turned into `dugUpGround` by
// a Command.Add straight into TileUpdateBuffer, with no health, no entity and no destroy gate for
// the mod to hold. Nothing in the damage pipeline can see it.
//
// That buffer is also how legitimate work happens — placing a floor, watering ground, plants
// growing, water spreading, chunk streaming. So this refuses an ENUMERATED SET and passes
// everything else. The safe direction is less protection, never a broken tilemap.
//
// THE HOE IS BLOCKED TOO, AND THAT IS THE DECISION, NOT AN OVERSIGHT. Tilling writes exactly the
// entry an explosion writes (Command.Add of dugUpGround) and the buffer carries no causer, so
// nothing separates them. design.md §4 settles it the way the rest of the mod is settled: to change
// the ground inside your base, switch the pylon off. The game agrees with this shape — its own
// immunity zones make the hoe and the shovel refuse (HoeSlot and ShovelSlot both test
// TileType.immune before acting).
public static class NoBreakZoneTileEdit
{
    // TileUpdateBuffer.Command. Passed as int so this file stays free of game types; the caller
    // casts the enum.
    public const int CommandAdd = 0;
    public const int CommandRemove = 1;
    public const int CommandClear = 2;

    // PugTilemap.TileType. Hardcoded rather than referenced for the same reason, and safe to
    // hardcode because tile types are written into the tilemap and therefore into saves.
    public const int TileGround = 46;
    public const int TileDugUpGround = 49;

    /// <param name="command">TileUpdateBuffer.Command as an int — Add, Remove or Clear.</param>
    /// <param name="tileType">PugTilemap.TileType as an int.</param>
    /// <param name="covered">Is this tile inside a switched-on pylon's square?</param>
    /// <param name="clearedAtSamePosition">
    /// Does an earlier entry in the same buffer Clear this position? Dropping the Add half of a
    /// Clear+Add pair would leave a hole where a tile used to be — see below.
    /// </param>
    /// <returns>True to drop the entry before the tilemap sees it.</returns>
    public static bool RefuseEdit(int command, int tileType, bool covered, bool clearedAtSamePosition)
    {
        if (!covered)
        {
            // Outside every square this is none of the mod's business.
            return false;
        }

        if (command != CommandAdd)
        {
            // COMMAND.REMOVE IS NEVER REFUSED, AND THAT IS A SAFETY RULE, NOT AN OMISSION.
            //
            // The obvious use for it is the shovel, which lifts a placed floor inside a protected
            // base. Blocking it here duplicates the floor. PlayerController.DigUpTile does two
            // things that do not travel together:
            //
            //     EntityUtility.RemoveTile(...)      -> TileUpdateBuffer   (this buffer)
            //     EntityUtility.CreateAndDropItem(...) -> a separate EntityCommandBuffer
            //
            // Only the first is ours to drop. Refuse it and the floor stays, the item still drops,
            // and the player can repeat that forever — which is precisely the resource duplication
            // design.md §6 forbids outright, and a far worse bug than the hole it would have
            // prevented.
            //
            // Blocking the shovel needs to happen where the decision is made, not where its output
            // lands. TileType.immune is the game's own mechanism for that; it writes into the save,
            // so it is an open question rather than a fix.
            //
            // Command.Clear falls here too, and must: chunk streaming clears tiles as the world
            // loads and unloads around the player. Refusing that corrupts the map.
            return false;
        }

        // The only Add worth refusing is the one that digs bare ground up. Everything else is
        // construction — placing a floor, watering, a plant spreading — and building inside your own
        // base has to keep working.
        //
        // EXCEPT WHEN A CLEAR CAME FIRST. EnsureSameGroundTileBeneathEntitySystem levels the ground
        // under a newly placed object by emitting Clear then Add at the same tile. The Clear goes
        // through (above), so dropping its Add would empty that tile — worse than the digging this
        // is trying to prevent.
        return tileType == TileDugUpGround && !clearedAtSamePosition;
    }
}
