using NUnit.Framework;

namespace NoBreakZone.Tests
{
    // 기획서 §6 judges a multi-tile object by every tile it stands on, so getting the footprint
    // wrong shifts the whole judgement by a tile — an object reported safe while a corner of it
    // sits outside the pylon's square.
    public class NoBreakZoneFootprintTests
    {
        [Test]
        public void SingleTileObjectOccupiesItsOwnTile()
        {
            NoBreakZoneFootprint.Rect(5, -3, 1, 1, 0, 0,
                out int minX, out int minZ, out int maxX, out int maxZ);

            Assert.AreEqual(5, minX);
            Assert.AreEqual(-3, minZ);
            Assert.AreEqual(5, maxX);
            Assert.AreEqual(-3, maxZ);
        }

        [Test]
        public void TwoByOneOccupiesTwoTilesNotThree()
        {
            // The game's range is half-open, [tile, tile + size). Returning an inclusive rectangle
            // means subtracting one, and forgetting that is how a 2×1 would claim three tiles.
            NoBreakZoneFootprint.Rect(0, 0, 2, 1, 0, 0,
                out int minX, out int minZ, out int maxX, out int maxZ);

            Assert.AreEqual(0, minX);
            Assert.AreEqual(1, maxX);
            Assert.AreEqual(0, minZ);
            Assert.AreEqual(0, maxZ);
        }

        [Test]
        public void CornerOffsetMovesTheWholeRectangle()
        {
            // prefabCornerOffset is why the origin tile is not necessarily one of the occupied
            // tiles — a wide object can be anchored at its centre.
            NoBreakZoneFootprint.Rect(10, 10, 3, 3, -1, -1,
                out int minX, out int minZ, out int maxX, out int maxZ);

            Assert.AreEqual(9, minX);
            Assert.AreEqual(9, minZ);
            Assert.AreEqual(11, maxX);
            Assert.AreEqual(11, maxZ);
        }

        [Test]
        public void ZeroOrNegativeSizeFallsBackToOneTile()
        {
            // Reached when the object database has nothing for this object. One tile keeps it
            // protectable; a degenerate rectangle would drop it out of protection silently, which
            // in game is indistinguishable from the pylon being broken.
            NoBreakZoneFootprint.Rect(4, 4, 0, 0, 0, 0,
                out int minX, out int minZ, out int maxX, out int maxZ);

            Assert.AreEqual(4, minX);
            Assert.AreEqual(4, minZ);
            Assert.AreEqual(4, maxX);
            Assert.AreEqual(4, maxZ);

            NoBreakZoneFootprint.Rect(4, 4, -5, -5, 0, 0, out minX, out minZ, out maxX, out maxZ);
            Assert.AreEqual(4, maxX);
            Assert.AreEqual(4, maxZ);
        }

        [Test]
        public void FootprintFeedsTheProtectionRuleForOurOwnWorkbench()
        {
            // The 2×1 Pylon Workbench is the first thing this mod ships that the old origin-only
            // check would have judged wrongly. Placed so its right tile falls outside a pylon at
            // the origin, it must not be protected — even though its origin tile is inside.
            NoBreakZoneFootprint.Rect(10, 0, 2, 1, 0, 0,
                out int minX, out int minZ, out int maxX, out int maxZ);

            var px = new[] { 0 };
            var pz = new[] { 0 };
            Assert.IsFalse(NoBreakZoneRange.AllTilesCovered(px, pz, 1, minX, minZ, maxX, maxZ, 10));

            // One tile further in and the whole thing fits.
            NoBreakZoneFootprint.Rect(9, 0, 2, 1, 0, 0, out minX, out minZ, out maxX, out maxZ);
            Assert.IsTrue(NoBreakZoneRange.AllTilesCovered(px, pz, 1, minX, minZ, maxX, maxZ, 10));
        }
    }
}
