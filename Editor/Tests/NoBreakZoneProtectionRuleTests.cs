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
        // 569 installations plus the 123 tiles the rule started protecting when walls and floors
        // came in (41 of them walls). Zero of the ten ore tiles, which is the number that matters.
        private const int ExpectedProtectedCount = 692;

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
        public void NonPlaceablesAndHealthlessThingsAreRejected()
        {
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                SomeOtherObjectType, true, false, false, false, false), "not a placeable");
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, hasHealth: false, isTile: false, false, false, false),
                "seeds and crops have no health and must stay harvestable");
        }

        [Test]
        public void AWallIsProtectedButAnythingMinedForMaterialIsNot()
        {
            // A wall's loot comes from its table when it dies, so blocking the death yields nothing
            // rather than yielding forever — which is why walls can be protected at all.
            Assert.IsTrue(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, isTile: true, false, dropsLootFromTable: true, false),
                "a wall is part of a base");

            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, isTile: true, false, false, false, isOreTile: true),
                "ore is mined for its material");
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, isTile: true, false, false, false, requiresDrill: true),
                "a drill target is a resource");
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, isTile: true, false, false, false, isPlant: true),
                "crops are harvested, not destroyed");
            Assert.IsFalse(NoBreakZoneProtectionRule.ShouldProtect(
                PlaceablePrefab, true, isTile: true, false, false, dropsLootWhenDamaged: true),
                "anything that pays out while being hit would duplicate");
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
                    row["lootOnDmg"] == "1",
                    isOreTile: row["tileType"] == "ore",
                    requiresDrill: row["requiresDrill"] == "1",
                    isPlant: row["plant"] == "1" || row["growing"] == "1");

                if (!result)
                {
                    continue;
                }

                string id = row["id"];
                protectedIds.Add(id);

                // The invariant that matters more than the count: a single leak here is a resource
                // duplication bug in a live save, and 기획서 §6 forbids that outright. Terrain is no
                // longer on this list — walls and floors are protected on purpose now — so what
                // guards it is the set of things mined FOR their material.
                Assert.AreNotEqual("1", row["lootOnDmg"], id + ": pays out while damaged");
                Assert.AreNotEqual("ore", row["tileType"], id + ": ore must stay mineable");
                Assert.AreNotEqual("1", row["requiresDrill"], id + ": drill targets are resources");
                Assert.AreNotEqual("1", row["plant"], id + ": crops must stay harvestable");
                Assert.AreNotEqual("1", row["growing"], id + ": crops must stay harvestable");
            }

            Assert.AreEqual(ExpectedProtectedCount, protectedIds.Count,
                "the set of protected objects changed — re-derive the rule before updating this number");
            CollectionAssert.Contains(protectedIds, "WoodenWorkBench");
            CollectionAssert.Contains(protectedIds, "BossChest");
            CollectionAssert.Contains(protectedIds, "WallStoneBlock");
            CollectionAssert.DoesNotContain(protectedIds, "CopperOreBoulder");
            CollectionAssert.DoesNotContain(protectedIds, "CopperOre");
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
