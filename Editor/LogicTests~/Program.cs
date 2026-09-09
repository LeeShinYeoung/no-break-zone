using System;
using System.Collections.Generic;
using System.IO;

namespace NoBreakZone.LogicTests
{
    /// <summary>
    /// Offline gate for everything in Scripts/Logic. Run through Editor/logictest.ps1.
    ///
    /// Exit code 0 = every check passed, 1 = at least one failed, 2 = could not run (missing input).
    /// Failures print as `[FAIL] &lt;what&gt;` so a build log can be grepped.
    ///
    /// This mirrors the NUnit suite in Editor/Tests rather than replacing it: the NUnit tests still
    /// work for a human in the editor, but they cannot be run from a script (see the csproj header),
    /// and a check nobody can run automatically is a check that rots. The row and protected counts
    /// are duplicated deliberately — if they ever disagree, one of the two is looking at a file the
    /// other is not.
    /// </summary>
    internal static class Program
    {
        private const int PlaceablePrefab = NoBreakZoneProtectionRule.PlaceablePrefabObjectType;
        private const int SomeOtherObjectType = 500; // ObjectType.Sword — anything not placeable

        // Editor/Tests/NoBreakZoneProtectionRuleTests.cs asserts the same two numbers.
        private const int ExpectedRowCount = 2278;
        private const int ExpectedProtectedCount = 692;

        private static int _failures;
        private static int _checks;

        private static int Main()
        {
            string csv = FindObjectFlagsCsv();
            if (csv == null)
            {
                Console.Error.WriteLine("[FAIL] object_flags.csv not found next to this project");
                return 2;
            }

            RangeChecks();
            RemoteReachChecks();
            FootprintChecks();
            ProtectionRuleUnitChecks();
            TileEditChecks();
            WholeDatabaseRegression(csv);

            Console.WriteLine(_failures == 0
                ? $"ALL PASS ({_checks} checks)"
                : $"{_failures} FAILURE(S) out of {_checks} checks");
            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ Scripts/Logic/NoBreakZoneRange

        private static void RangeChecks()
        {
            // 기획서 §6: the pylon owns the centre tile, so a diameter of 21 reaches ten tiles out.
            IsTrue(NoBreakZoneRange.RadiusFromDiameter(21) == 10, "diameter 21 -> radius 10");
            IsTrue(NoBreakZoneRange.RadiusFromDiameter(22) == 10, "an even diameter rounds down");
            IsTrue(NoBreakZoneRange.RadiusFromDiameter(0) == 0, "a degenerate diameter is not negative");

            int[] px = { 0 };
            int[] pz = { 0 };

            IsTrue(Covered(px, pz, 1, 10, 10, 10), "the far corner is inside the square");
            IsTrue(!Covered(px, pz, 1, 11, 0, 10), "one tile past the edge is outside");
            IsTrue(Covered(px, pz, 1, -10, -10, 10), "the square is symmetric");

            // Two pylons may each cover part of one object; 기획서 §6 wants the union to count.
            // 21 apart with radius 10 makes the squares [-10,10] and [11,31] — touching, no gap.
            int[] touchingX = { 0, 21 };
            int[] touchingZ = { 0, 0 };
            IsTrue(NoBreakZoneRange.AllTilesCovered(touchingX, touchingZ, 2, 10, 0, 11, 0, 10),
                "a footprint straddling two touching squares is covered by their union");

            // One tile further apart and x=11 belongs to neither square.
            int[] gappedX = { 0, 22 };
            int[] gappedZ = { 0, 0 };
            IsTrue(!NoBreakZoneRange.AllTilesCovered(gappedX, gappedZ, 2, 10, 0, 12, 0, 10),
                "a footprint spanning the gap between two squares is not covered");
            IsTrue(NoBreakZoneRange.AllTilesCovered(gappedX, gappedZ, 2, 12, 0, 12, 0, 10),
                "the far side of the gap is still covered by the second pylon");
        }

        private static bool Covered(int[] px, int[] pz, int count, int x, int z, int radius)
        {
            return NoBreakZoneRange.AllTilesCovered(px, pz, count, x, z, x, z, radius);
        }

        // ---------------------------------------------- Scripts/Logic/NoBreakZoneRange.IsWithinReach

        /// 기획서 §4's remote: right-click a pylon from up to 30 tiles away and it switches.
        ///
        /// coverage.md counted #35 and #36 as work for the in-game suite, on the grounds that the
        /// remote runs on the server. That was the wrong reason: the REACH DECISION is a pure
        /// function of two coordinates and a number, so it needs no game, no server and no human.
        /// Only the input path — reading a real player's cursor — needs those.
        private static void RemoteReachChecks()
        {
            const int reach = NoBreakZoneRange.DefaultRemoteReach;

            IsTrue(reach == 30, "the remote reaches 30 tiles by default");

            IsTrue(NoBreakZoneRange.IsWithinReach(0, 0, 30, 0, reach),
                "a pylon exactly 30 tiles away is in reach");
            IsTrue(!NoBreakZoneRange.IsWithinReach(0, 0, 31, 0, reach),
                "one tile further is not");

            // ROUND, NOT SQUARE, and this is the check that holds it that way. A square would quietly
            // give 41% more reach on the diagonal than along an axis — the reason IsWithinReach
            // compares squared distances rather than max(|dx|,|dz|) the way the protection square
            // does. (21,21) is 29.7 tiles out; (22,22) is 31.1.
            IsTrue(NoBreakZoneRange.IsWithinReach(0, 0, 21, 21, reach),
                "the diagonal is measured as a circle: 21,21 is inside");
            IsTrue(!NoBreakZoneRange.IsWithinReach(0, 0, 22, 22, reach),
                "and 22,22 is outside, where a square would have let it through");

            IsTrue(NoBreakZoneRange.IsWithinReach(0, 0, 0, 0, reach),
                "standing on the pylon is in reach");
            IsTrue(NoBreakZoneRange.IsWithinReach(-30, 0, 0, 0, reach),
                "reach is symmetric about the player");

            IsTrue(!NoBreakZoneRange.IsWithinReach(0, 0, 0, 0, -1),
                "a negative reach touches nothing, not even its own tile");

            // 기획서 §4 #36, "벽 너머로도 통한다", and it does not look like the others because the
            // claim is structural rather than numeric. There is no line-of-sight input to this
            // decision — the arguments are two positions and a distance, and nothing else can be
            // consulted. Walls cannot matter because there is nowhere for them to enter.
            //
            // What this pins down is that it stays that way. If somebody later adds an obstruction
            // test, the call below stops compiling or stops answering true, and the design decision
            // gets revisited on purpose instead of by accident.
            IsTrue(NoBreakZoneRange.IsWithinReach(0, 0, 20, 0, reach),
                "reach ignores what stands between: distance is the only input");
        }

        // -------------------------------------------------------------- Scripts/Logic/NoBreakZoneFootprint

        private static void FootprintChecks()
        {
            NoBreakZoneFootprint.Rect(5, 5, 1, 1, 0, 0, out int minX, out int minZ, out int maxX, out int maxZ);
            IsTrue(minX == 5 && minZ == 5 && maxX == 5 && maxZ == 5, "a 1x1 object occupies its own tile");

            NoBreakZoneFootprint.Rect(5, 5, 2, 1, 0, 0, out minX, out minZ, out maxX, out maxZ);
            IsTrue(minX == 5 && maxX == 6 && minZ == 5 && maxZ == 5, "a 2x1 object spans two tiles");

            NoBreakZoneFootprint.Rect(5, 5, 2, 2, -1, -1, out minX, out minZ, out maxX, out maxZ);
            IsTrue(minX == 4 && minZ == 4 && maxX == 5 && maxZ == 5, "the corner offset shifts the rect");

            NoBreakZoneFootprint.Rect(5, 5, 0, 0, 0, 0, out minX, out minZ, out maxX, out maxZ);
            IsTrue(minX == 5 && maxX == 5 && minZ == 5 && maxZ == 5, "a zero size falls back to one tile");
        }

        // ----------------------------------------------------------- Scripts/Logic/NoBreakZoneProtectionRule

        private static void ProtectionRuleUnitChecks()
        {
            IsTrue(NoBreakZoneProtectionRule.ShouldProtect(
                    PlaceablePrefab, hasHealth: true, isTile: false,
                    isDestructibleObject: false, dropsLootFromTable: false, dropsLootWhenDamaged: false),
                "a chest is protected");

            IsTrue(!NoBreakZoneProtectionRule.ShouldProtect(
                    PlaceablePrefab, hasHealth: true, isTile: false,
                    isDestructibleObject: true, dropsLootFromTable: false, dropsLootWhenDamaged: true),
                "an ore boulder is not");

            IsTrue(!NoBreakZoneProtectionRule.ShouldProtect(
                    SomeOtherObjectType, true, false, false, false, false),
                "a sword is not a placeable");

            IsTrue(!NoBreakZoneProtectionRule.ShouldProtect(
                    PlaceablePrefab, hasHealth: false, isTile: false, false, false, false),
                "seeds and crops have no health and stay harvestable");

            IsTrue(NoBreakZoneProtectionRule.ShouldProtect(
                    PlaceablePrefab, true, isTile: true, false, dropsLootFromTable: true, false),
                "a wall is part of a base");

            IsTrue(!NoBreakZoneProtectionRule.ShouldProtect(
                    PlaceablePrefab, true, isTile: true, false, false, false, isOreTile: true),
                "ore is mined for its material");
        }

        // ------------------------------------------------------------------ Scripts/Logic/NoBreakZoneTileEdit

        private static void TileEditChecks()
        {
            const int add = NoBreakZoneTileEdit.CommandAdd;
            const int remove = NoBreakZoneTileEdit.CommandRemove;
            const int clear = NoBreakZoneTileEdit.CommandClear;
            const int dug = NoBreakZoneTileEdit.TileDugUpGround;
            const int ground = NoBreakZoneTileEdit.TileGround;
            const int floor = 64;   // TileType.floor
            const int ore = 129;    // TileType.ore

            // The one refusal the whole policy exists for.
            IsTrue(NoBreakZoneTileEdit.RefuseEdit(add, dug, covered: true, clearedAtSamePosition: false),
                "digging bare ground inside the square is refused");

            IsTrue(!NoBreakZoneTileEdit.RefuseEdit(add, dug, covered: false, clearedAtSamePosition: false),
                "digging outside every square is allowed");

            IsTrue(!NoBreakZoneTileEdit.RefuseEdit(add, dug, covered: true, clearedAtSamePosition: true),
                "a Clear+Add pair is left alone so the tile is not emptied");

            // Removing is never refused — PlayerController.DigUpTile drops the item through a
            // separate command buffer, so refusing the removal duplicates the floor (기획서 §6).
            IsTrue(!NoBreakZoneTileEdit.RefuseEdit(remove, floor, covered: true, clearedAtSamePosition: false),
                "lifting a floor is allowed: refusing it would duplicate the item");
            IsTrue(!NoBreakZoneTileEdit.RefuseEdit(remove, ground, covered: true, clearedAtSamePosition: false),
                "removing ground is allowed for the same reason");

            IsTrue(!NoBreakZoneTileEdit.RefuseEdit(clear, ground, covered: true, clearedAtSamePosition: false),
                "chunk streaming's Clear is always allowed");

            IsTrue(!NoBreakZoneTileEdit.RefuseEdit(add, floor, covered: true, clearedAtSamePosition: false),
                "placing a floor inside your own base still works");
            IsTrue(!NoBreakZoneTileEdit.RefuseEdit(add, ore, covered: true, clearedAtSamePosition: false),
                "adding an ore tile is not our business");

            // Exhaustive: across every command, every tile type the game defines, and both flags,
            // exactly one shape may be refused. A new refusal added without a test lands here.
            int refusals = 0;
            int cases = 0;
            for (int command = 0; command <= 2; command++)
            {
                for (int tileType = 0; tileType < 135; tileType++)
                {
                    for (int covered = 0; covered < 2; covered++)
                    {
                        for (int cleared = 0; cleared < 2; cleared++)
                        {
                            cases++;
                            if (!NoBreakZoneTileEdit.RefuseEdit(command, tileType, covered == 1, cleared == 1))
                            {
                                continue;
                            }

                            refusals++;
                            bool expected = command == add && tileType == dug && covered == 1 && cleared == 0;
                            if (!expected)
                            {
                                _failures++;
                                Console.Error.WriteLine(
                                    $"[FAIL] unexpected refusal: command={command} tile={tileType} "
                                    + $"covered={covered == 1} cleared={cleared == 1}");
                            }
                        }
                    }
                }
            }

            IsTrue(refusals == 1, $"exactly one of {cases} edit shapes is refused (found {refusals})");
        }

        private static void WholeDatabaseRegression(string csvPath)
        {
            List<Dictionary<string, string>> rows = LoadObjectFlags(csvPath);
            IsTrue(rows.Count == ExpectedRowCount,
                $"object_flags.csv has {ExpectedRowCount} rows (found {rows.Count})");

            var protectedIds = new List<string>();
            int leaks = 0;

            foreach (Dictionary<string, string> row in rows)
            {
                bool result = NoBreakZoneProtectionRule.ShouldProtect(
                    row["type"] == "PlaceablePrefab" ? PlaceablePrefab : SomeOtherObjectType,
                    row["health"] == "1",
                    row["tileCD"] == "1",
                    row["destructible"] == "1",
                    row["lootTable"] == "1",
                    row["lootOnDmg"] == "1",
                    isOreTile: row["tileType"] == "ore",
                    requiresDrill: row["requiresDrill"] == "1",
                    isPlant: row["plant"] == "1" || row["growing"] == "1");

                if (!result)
                {
                    continue;
                }

                protectedIds.Add(row["id"]);

                // The invariant that outranks the count: one leak here duplicates a resource forever
                // in a live save, which 기획서 §6 forbids outright.
                if (row["lootOnDmg"] == "1" || row["tileType"] == "ore"
                    || row["requiresDrill"] == "1" || row["plant"] == "1" || row["growing"] == "1")
                {
                    leaks++;
                    Console.Error.WriteLine($"[FAIL] {row["id"]}: protected but mined for material");
                    _failures++;
                }
            }

            _checks++;
            if (leaks == 0)
            {
                Console.WriteLine("  ok: no protected object pays out while being mined");
            }

            IsTrue(protectedIds.Count == ExpectedProtectedCount,
                $"{ExpectedProtectedCount} objects protected (found {protectedIds.Count}) — "
                + "re-derive the rule before changing this number");

            IsTrue(protectedIds.Contains("WoodenWorkBench"), "the workbench is protected");
            IsTrue(protectedIds.Contains("BossChest"), "a boss chest is protected");
            IsTrue(protectedIds.Contains("WallStoneBlock"), "a wall is protected");
            IsTrue(!protectedIds.Contains("CopperOreBoulder"), "an ore boulder is not protected");
            IsTrue(!protectedIds.Contains("CopperOre"), "ore is not protected");
        }

        // ------------------------------------------------------------------------------------ plumbing

        private static void IsTrue(bool condition, string what)
        {
            _checks++;
            if (condition)
            {
                Console.WriteLine("  ok: " + what);
                return;
            }

            _failures++;
            Console.Error.WriteLine("[FAIL] " + what);
        }

        /// Walks up from the built assembly to the repository root. The project file is compiled from
        /// Editor/LogicTests~, so the CSV is a fixed two levels up — but the search is by directory
        /// name rather than by depth so a changed output path does not break it.
        private static string FindObjectFlagsCsv()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "Editor", "GameData", "object_flags.csv");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return null;
        }

        /// Same parsing rule as Editor/Tests/NoBreakZoneProtectionRuleTests.LoadObjectFlags: split on
        /// commas, skip blank lines, no quoting (the dump never emits a comma inside a field).
        private static List<Dictionary<string, string>> LoadObjectFlags(string path)
        {
            string[] lines = File.ReadAllLines(path);
            string[] header = lines[0].Split(',');
            var rows = new List<Dictionary<string, string>>(lines.Length);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] cells = lines[i].Split(',');
                var row = new Dictionary<string, string>(header.Length);
                for (int c = 0; c < header.Length && c < cells.Length; c++)
                {
                    row[header[c]] = cells[c];
                }

                rows.Add(row);
            }

            return rows;
        }
    }
}
