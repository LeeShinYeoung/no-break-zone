// Pure form of the "is this thing a player installation worth protecting?" discriminator, lifted out
// of NoBreakZoneProtectionSystem so it can be regression tested against the full object database
// (Editor/GameData/object_flags.csv, 2282 rows) instead of only being exercised in game.
//
// Deliberately free of any game type: callers pass component-presence booleans. That is also exactly
// the shape of the CSV, so the test can replay every object in the game through this function.
//
// The rule was derived from the CSV, not guessed per object. 기획서 §6 phrases it as "부수면 그
// 물건이 그대로 돌아오는가": a chest gives back a chest, an ore boulder gives back ore. The three
// loot flags are what separates the two, and getting this wrong duplicates resources — the one
// failure the design forbids outright (기획서 §6 "절대 발생해서는 안 되는 것").
public static class NoBreakZoneProtectionRule
{
    // ObjectType.PlaceablePrefab. Hardcoded rather than referenced so this file stays game-type free;
    // the value is stable because object types are written into saves.
    public const int PlaceablePrefabObjectType = 800;

    /// <param name="objectType">ObjectType from the database (ObjectTypeCD is absent on modded objects).</param>
    /// <param name="hasHealth">HealthCD — the entity is on the damage/destroy pipeline at all.</param>
    /// <param name="isTile">TileCD — terrain, walls, floors. Judged by the tile branch below.</param>
    /// <param name="isDestructibleObject">DestructibleObjectCD — world destructibles (ore, pots, barrels).</param>
    /// <param name="dropsLootFromTable">DropsLootFromLootTableCD — pots, ancient destructibles, walls.</param>
    /// <param name="dropsLootWhenDamaged">DropsLootWhenDamagedCD — ore boulders. The resource-dupe flag.</param>
    /// <param name="isOreTile">TileCD.tileType == ore. Mined for material and never a wall.</param>
    /// <param name="requiresDrill">RequiresDrillCD — the drill's targets, i.e. resources.</param>
    /// <param name="isPlant">PlantCD or GrowingCD — crops, harvested rather than destroyed.</param>
    public static bool ShouldProtect(
        int objectType,
        bool hasHealth,
        bool isTile,
        bool isDestructibleObject,
        bool dropsLootFromTable,
        bool dropsLootWhenDamaged,
        bool isOreTile = false,
        bool requiresDrill = false,
        bool isPlant = false)
    {
        if (!hasHealth)
        {
            return false;
        }

        if (isTile)
        {
            // A WALL AND A FLOOR ARE PART OF A BASE, AND A PLAYER WHO WALLED THEIR BASE IN EXPECTS
            // THE WALL TO SURVIVE (design.md §4's decision record). Tiles were excluded outright
            // until now for fear of resource duplication, and the fear was aimed at the wrong thing:
            // a wall's loot comes from its loot table on death, not while it is being hit
            // (object_flags.csv: all 41 walls are lootTable=1, lootOnDmg=0). Blocking the death
            // therefore yields nothing at all rather than yielding forever.
            //
            // What must never be protected is anything mined FOR its material, because those are the
            // ones that can pay out while refusing to die. Ore is excluded by tile type, drill
            // targets by their own component, and anything that drops while damaged by the flag that
            // says so — the same flag that has always guarded this rule.
            //
            // 기획서 §6: "자원 복제는 절대 발생해서는 안 된다." This branch is the only place in the
            // mod where that could go wrong, so it refuses on any one of the four.
            return !isOreTile && !requiresDrill && !isPlant && !dropsLootWhenDamaged;
        }

        if (objectType != PlaceablePrefabObjectType)
        {
            return false;
        }

        // Anything that yields something other than itself is a resource, not an installation.
        return !isDestructibleObject && !dropsLootFromTable && !dropsLootWhenDamaged;
    }
}
