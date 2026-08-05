using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace NoBreakZone.Tests
{
    // The discriminator decides what the mod makes indestructible. Getting it wrong in one direction
    // locks players out of remodelling; in the other it protects ore boulders, which never deplete
    // and therefore duplicate resources forever — 기획서 §6 calls that "절대 발생해서는 안 되는 것"
    // and ranks verifying it above every other feature.
    //
    // These tests replay the ENTIRE object database through the rule. object_flags.csv is a dump of
    // all 2282 objects with their component flags (see Editor/GameData/README.md), so this is the
    // real population, not a sample. The counts below are the values the rule was designed against;
    // if a game update moves them, that is a signal to re-derive the rule, not to edit the numbers.
    public class NoBreakZoneProtectionRuleTests
    {
        private const int PlaceablePrefab = NoBreakZoneProtectionRule.PlaceablePrefabObjectType;
        private const int SomeOtherObjectType = 500; // ObjectType.Sword — anything that is not placeable

        private const int ExpectedRowCount = 2282;
        private const int ExpectedProtectedCount = 569;

        [Test]
        public void AChestIsProtected()
        {
            Assert.IsTrue(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, hasHealth: true, isTile: false,
                isDestructibleObject: false, dropsLootFromTable: false, dropsLootWhenDamaged: false));
        }

        [Test]
        public void AnOreBoulderIsNotProtected()
        {
            // CopperOreBoulder: placeable and has health, but drops loot when damaged. Protecting it
            // would make a drill mine it forever.
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, hasHealth: true, isTile: false,
                isDestructibleObject: true, dropsLootFromTable: false, dropsLootWhenDamaged: true));
        }

        [Test]
        public void AnyOneLootFlagIsEnoughToDisqualify()
        {
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, false, isDestructibleObject: true,
                dropsLootFromTable: false, dropsLootWhenDamaged: false));
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, false, isDestructibleObject: false,
                dropsLootFromTable: true, dropsLootWhenDamaged: false));
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, false, isDestructibleObject: false,
                dropsLootFromTable: false, dropsLootWhenDamaged: true));
        }

        [Test]
        public void TerrainAndNonPlaceablesAndHealthlessThingsAreAllRejected()
        {
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, isTile: true, false, false, false), "tiles are terrain");
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                SomeOtherObjectType, true, false, false, false, false), "not a placeable");
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, hasHealth: false, isTile: false, false, false, false),
                "seeds and crops have no health and must stay harvestable");
        }

        [Test]
        public void WholeDatabaseRegression()
        {
            var rows = LoadObjectFlags();
            Assert.AreEqual(ExpectedRowCount, rows.Count, "object_flags.csv row count changed");

            var protectedIds = new List<string>();
            foreach (var row in rows)
            {
                bool result = NoBreakZoneProtectionRule.ShouldProtect(
                    row["type"] == "PlaceablePrefab" ? PlaceablePrefab : SomeOtherObjectType,
                    row["health"] == "1",
                    row["tileCD"] == "1",
                    row["destructible"] == "1",
                    row["lootTable"] == "1",
                    row["lootOnDmg"] == "1");

                if (!result)
                {
                    continue;
                }

                string id = row["id"];
                protectedIds.Add(id);

                // The two invariants that matter more than the count. A single leak here is a
                // resource duplication bug in a live save.
                Assert.AreNotEqual("1", row["tileCD"], id + ": terrain must never be protected");
                Assert.AreNotEqual("1", row["lootOnDmg"], id + ": ore boulders must stay mineable");
            }

            Assert.AreEqual(ExpectedProtectedCount, protectedIds.Count,
                "the set of protected objects changed — re-derive the rule before updating this number");
            CollectionAssert.Contains(protectedIds, "WoodenWorkBench");
            CollectionAssert.Contains(protectedIds, "BossChest");
            CollectionAssert.DoesNotContain(protectedIds, "CopperOreBoulder");
        }

        // Located through AssetDatabase rather than a hardcoded path: this repository is checked out
        // inside the SDK project as Assets/NoBreakZone, but nothing guarantees that folder name.
        private static List<Dictionary<string, string>> LoadObjectFlags()
        {
            string[] guids = AssetDatabase.FindAssets("object_flags");
            string path = null;
            foreach (string guid in guids)
            {
                string candidate = AssetDatabase.GUIDToAssetPath(guid);
                if (candidate.EndsWith("GameData/object_flags.csv"))
                {
                    path = candidate;
                    break;
                }
            }

            Assert.IsNotNull(path, "object_flags.csv not found in the project");

            var lines = File.ReadAllLines(path);
            var header = lines[0].Split(',');
            var rows = new List<Dictionary<string, string>>(lines.Length);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                var cells = lines[i].Split(',');
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
