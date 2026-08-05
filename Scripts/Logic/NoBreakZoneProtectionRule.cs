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

    /// <param name="objectType">ObjectTypeCD.Value as an int.</param>
    /// <param name="hasHealth">HealthCD — the entity is on the damage/destroy pipeline at all.</param>
    /// <param name="isTile">TileCD — terrain, walls, floors. Never ours to protect.</param>
    /// <param name="isDestructibleObject">DestructibleObjectCD — world destructibles (ore, pots, barrels).</param>
    /// <param name="dropsLootFromTable">DropsLootFromLootTableCD — pots, ancient destructibles, walls.</param>
    /// <param name="dropsLootWhenDamaged">DropsLootWhenDamagedCD — ore boulders. The resource-dupe flag.</param>
    public static bool ShouldProtect(
        int objectType,
        bool hasHealth,
        bool isTile,
        bool isDestructibleObject,
        bool dropsLootFromTable,
        bool dropsLootWhenDamaged)
    {
        if (!hasHealth || isTile)
        {
            return false;
        }

        if (objectType != PlaceablePrefabObjectType)
        {
            return false;
        }

        // Anything that yields something other than itself is a resource, not an installation.
        return !isDestructibleObject && !dropsLootFromTable && !dropsLootWhenDamaged;
    }
}
