using PugMod;
using UnityEngine;

// design.md §10's four settings, and the only place that reads them.
//
// The game owns the file: API.Config decides where it lives and what it looks like, so there is no
// Conf/ folder of ours to create. Registration happens once from NoBreakZoneMod.Init.
//
// EVERY READ FALLS BACK TO THE COMPILED DEFAULT. If registration never ran, or the API is
// unavailable in whatever context a system happens to be running in, the mod behaves exactly as it
// did before settings existed. A configuration system that can take protection down with it is a
// worse trade than one that quietly ignores a setting.
public static class NoBreakZoneConfig
{
    // API.Config keys are (mod, section, key). The mod name has to match what the game knows this
    // mod as, which is the name in Data/NoBreakZone.asset.
    private const string ModName = "NoBreakZone";
    private const string Section = "General";

    private static IConfigEntry<int> _protectionDiameter;
    private static IConfigEntry<bool> _blockMobDamage;
    private static IConfigEntry<bool> _showRangeWithLens;
    private static IConfigEntry<int> _remoteReachTiles;
    private static IConfigEntry<bool> _selfTest;
    private static IConfigEntry<bool> _lensDebug;

    /// 기획서 §6. Odd by nature — the pylon owns the centre tile.
    public static int ProtectionDiameter =>
        Read(_protectionDiameter, NoBreakZoneRange.DefaultDiameter);

    /// design.md §10: off means only player-dealt damage is stopped.
    ///
    /// It maps onto the two components the protection already uses, because they guard different
    /// paths (research.md 8·9장): IndestructibleCD is what the player's own mining and attacks
    /// consult, while DontDestroyOnZeroHealthCD guards the single gate every damage source passes
    /// through. Dropping the second one leaves mobs and explosions able to finish something off.
    public static bool BlockMobDamage => Read(_blockMobDamage, true);

    /// 기획서 §7 ties the range display to the lens and nothing else; this turns even that off.
    public static bool ShowRangeWithLens => Read(_showRangeWithLens, true);

    /// design.md §4: 30 tiles, measured as a radius from the player.
    public static int RemoteReachTiles =>
        Read(_remoteReachTiles, NoBreakZoneRange.DefaultRemoteReach);

    /// Runs the built-in tile protection self test and writes [NBZTEST] lines to Player.log.
    ///
    /// OFF BY DEFAULT AND MEANT TO STAY THAT WAY. It deliberately damages tiles around a pylon,
    /// including outside the square where they are supposed to break, so it belongs in a world
    /// nobody minds losing. What it buys is that the one thing no offline check can reach — whether
    /// the mod wins the frame-order race against the game's real damage pipeline — becomes a line
    /// in a log instead of a play session.
    public static bool SelfTest => Read(_selfTest, false);

    /// Draws the lens range markers deliberately, unmissably wrong: opaque magenta, thick, and
    /// lifted well clear of the ground.
    ///
    /// 기획서 §7's range display has been reported as "nothing happens" twice while the log said the
    /// markers were being created and drawn. That leaves only how they are drawn, and the cheapest
    /// way to split "drawn but invisible" from "not drawn at all" is to make them impossible to miss
    /// and see whether anything appears. If it does, the fault is in the appearance we chose —
    /// rotation, colour or height. If it does not, it is somewhere deeper and worth a session of its
    /// own rather than another guess.
    ///
    /// A setting rather than a build flag so one connection can answer both halves: play with it
    /// off, then on, without rebuilding or rejoining.
    public static bool LensDebugMarkers => Read(_lensDebug, false);

    public static void Register()
    {
        if (API.Config == null)
        {
            Debug.LogWarning("[NoBreakZone] no config API — running on built-in defaults");
            return;
        }

        _protectionDiameter = API.Config.Register(
            ModName, Section,
            "Width and height, in tiles, of the square one pylon protects. The pylon stands in the "
            + "middle, so an even number rounds down.",
            "protectionDiameter", NoBreakZoneRange.DefaultDiameter);

        _blockMobDamage = API.Config.Register(
            ModName, Section,
            "Stop every source of damage. Turn off to stop only what the player does, leaving mobs "
            + "and explosions able to destroy protected objects.",
            "blockMobDamage", true);

        _showRangeWithLens = API.Config.Register(
            ModName, Section,
            "Draw the protected area's edge while the Pylon Lens is held.",
            "showRangeWithLens", true);

        _remoteReachTiles = API.Config.Register(
            ModName, Section,
            "How far, in tiles, the Pylon Remote reaches.",
            "remoteReachTiles", NoBreakZoneRange.DefaultRemoteReach);

        _lensDebug = API.Config.Register(
            ModName, Section,
            "Draw the Pylon Lens range markers in an unmissable debug form. For working out why the "
            + "range outline is not showing; leave off for normal play.",
            "lensDebugMarkers", false);

        _selfTest = API.Config.Register(
            ModName, Section,
            "Run the built-in tile protection self test on world load and write [NBZTEST] lines to "
            + "the log. It damages tiles around a pylon on purpose — use a throwaway world.",
            "selfTest", false);

        Debug.Log($"[NoBreakZone] config: diameter={ProtectionDiameter} "
                  + $"blockMobDamage={BlockMobDamage} showRange={ShowRangeWithLens} "
                  + $"remoteReach={RemoteReachTiles}");
    }

    private static T Read<T>(IConfigEntry<T> entry, T fallback)
    {
        return entry == null ? fallback : entry.Value;
    }
}
