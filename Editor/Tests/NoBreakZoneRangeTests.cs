using NUnit.Framework;

namespace NoBreakZone.Tests
{
    // 기획서 §6 defines the protection area as a SQUARE of N×N tiles, N=21, judged by "the larger of
    // the horizontal and vertical distance is at most 10". Every off-by-one here is a tile of base
    // that silently is or is not protected, which is exactly the failure the design calls its worst
    // (§3-2: "켜놓은 줄 알았는데 꺼져 있었다").
    public class NoBreakZoneRangeTests
    {
        [Test]
        public void DefaultDiameterMatchesTheDesignDocument()
        {
            Assert.AreEqual(21, NoBreakZoneRange.DefaultDiameter);
            Assert.AreEqual(10, NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneRange.DefaultDiameter));
        }

        [Test]
        public void EvenDiameterRoundsDownRatherThanShiftingTheCentre()
        {
            // The pylon owns the centre tile, so a diameter is conceptually always odd. 22 must not
            // quietly become "10 one way, 11 the other".
            Assert.AreEqual(10, NoBreakZoneRange.RadiusFromDiameter(22));
        }

        [Test]
        public void DegenerateDiametersClampToZero()
        {
            Assert.AreEqual(0, NoBreakZoneRange.RadiusFromDiameter(1));
            Assert.AreEqual(0, NoBreakZoneRange.RadiusFromDiameter(0));
            Assert.AreEqual(0, NoBreakZoneRange.RadiusFromDiameter(-5));
        }

        [Test]
        public void PylonCoversItsOwnTile()
        {
            Assert.IsTrue(NoBreakZoneRange.Covers(4, -7, 4, -7, 10));
        }

        [Test]
        public void EdgeOfTheSquareIsInclusive()
        {
            // Radius 10 means the 10th tile out is still protected and the 11th is not.
            Assert.IsTrue(NoBreakZoneRange.Covers(0, 0, 10, 0, 10));
            Assert.IsTrue(NoBreakZoneRange.Covers(0, 0, 0, -10, 10));
            Assert.IsFalse(NoBreakZoneRange.Covers(0, 0, 11, 0, 10));
            Assert.IsFalse(NoBreakZoneRange.Covers(0, 0, 0, -11, 10));
        }

        [Test]
        public void CornersAreCoveredBecauseTheShapeIsASquareNotACircle()
        {
            // (10,10) is ~14.1 tiles away by Euclidean distance. A circle would exclude it; the
            // design explicitly wants a square, so it must be inside.
            Assert.IsTrue(NoBreakZoneRange.Covers(0, 0, 10, 10, 10));
            Assert.IsTrue(NoBreakZoneRange.Covers(0, 0, -10, 10, 10));
            Assert.IsFalse(NoBreakZoneRange.Covers(0, 0, 11, 10, 10));
        }

        [Test]
        public void CoverageIsRelativeToThePylonNotTheOrigin()
        {
            Assert.IsTrue(NoBreakZoneRange.Covers(1000, -1000, 1010, -1010, 10));
            Assert.IsFalse(NoBreakZoneRange.Covers(1000, -1000, 1011, -1000, 10));
        }

        [Test]
        public void NegativeRadiusCoversNothing()
        {
            Assert.IsFalse(NoBreakZoneRange.Covers(0, 0, 0, 0, -1));
        }

        // --- 기획서 §6's multi-tile rule ------------------------------------------------------

        private static bool Covered(int[] px, int[] pz, int minX, int minZ, int maxX, int maxZ)
        {
            return NoBreakZoneRange.AllTilesCovered(px, pz, px.Length, minX, minZ, maxX, maxZ, 10);
        }

        [Test]
        public void MultiTileObjectNeedsEveryTileInside()
        {
            // 기획서 §6: "모든 타일이 범위 안에 들어와야 보호된다. 한 칸이라도 밖으로 나가면
            // 보호되지 않는다." A 2×1 workbench straddling the boundary is not protected.
            var px = new[] { 0 };
            var pz = new[] { 0 };
            Assert.IsTrue(Covered(px, pz, 9, 0, 10, 0));
            Assert.IsFalse(Covered(px, pz, 10, 0, 11, 0));
        }

        [Test]
        public void TilesMayBeCoveredByDifferentPylons()
        {
            // The other half of §6: "범위가 겹쳐도 문제없다. 어느 하나의 켜진 파일런 범위 안에
            // 있으면 보호된다." Two pylons 20 apart, so their squares meet at x=10/x=11 without
            // overlapping. A 2×1 lying across that seam has each tile covered by a different
            // pylon, and is protected.
            var px = new[] { 0, 21 };
            var pz = new[] { 0, 0 };
            Assert.IsTrue(Covered(px, pz, 10, 0, 11, 0));

            // Remove the second pylon and the same object is no longer protected — which is what
            // makes this a real test of the union rule rather than of the geometry.
            Assert.IsFalse(Covered(new[] { 0 }, new[] { 0 }, 10, 0, 11, 0));
        }

        [Test]
        public void AGapBetweenPylonsIsStillAGap()
        {
            // Far enough apart that x=11 belongs to neither. Union coverage must not paper over
            // the hole just because both ends of the object are covered.
            var px = new[] { 0, 22 };
            var pz = new[] { 0, 0 };
            Assert.IsFalse(Covered(px, pz, 10, 0, 12, 0));
        }

        [Test]
        public void SingleTileRectBehavesLikeASingleTile()
        {
            var px = new[] { 0 };
            var pz = new[] { 0 };
            Assert.IsTrue(Covered(px, pz, 10, 10, 10, 10));
            Assert.IsFalse(Covered(px, pz, 11, 11, 11, 11));
        }

        [Test]
        public void InvertedRectIsRejectedRatherThanSilentlyAccepted()
        {
            var px = new[] { 0 };
            var pz = new[] { 0 };
            Assert.IsFalse(Covered(px, pz, 5, 0, 4, 0));
            Assert.IsFalse(Covered(px, pz, 0, 5, 0, 4));
        }

        [Test]
        public void NoPylonsProtectsNothing()
        {
            Assert.IsFalse(NoBreakZoneRange.AllTilesCovered(
                new[] { 0 }, new[] { 0 }, 0, 0, 0, 0, 0, 10));
            Assert.IsFalse(NoBreakZoneRange.AllTilesCovered(null, null, 1, 0, 0, 0, 0, 10));
        }

        [Test]
        public void CountBeyondTheBufferIsClampedRatherThanReadPastTheEnd()
        {
            // The overlay and the protection system both hand in a reused buffer with a live count;
            // a stale count must not walk off the end.
            var px = new[] { 0 };
            var pz = new[] { 0 };
            Assert.IsTrue(NoBreakZoneRange.AllTilesCovered(px, pz, 99, 0, 0, 0, 0, 10));
        }

        // --- the remote's reach (기획서 §4) ---------------------------------------------------

        [Test]
        public void RemoteReachMatchesTheDesignDocument()
        {
            Assert.AreEqual(30, NoBreakZoneRange.DefaultRemoteReach);
        }

        [Test]
        public void ReachIsRoundSoTheDiagonalIsNotLonger()
        {
            // A square reach would give 41% more range on the diagonal than along an axis, which is
            // not what "플레이어 기준 30타일 반경" says. 30 out along an axis is in; 30 out on both
            // axes at once is not.
            Assert.IsTrue(NoBreakZoneRange.IsWithinReach(0, 0, 30, 0, 30));
            Assert.IsTrue(NoBreakZoneRange.IsWithinReach(0, 0, 0, -30, 30));
            Assert.IsFalse(NoBreakZoneRange.IsWithinReach(0, 0, 30, 30, 30));

            // 21,21 is 29.7 away — inside. 22,22 is 31.1 — outside.
            Assert.IsTrue(NoBreakZoneRange.IsWithinReach(0, 0, 21, 21, 30));
            Assert.IsFalse(NoBreakZoneRange.IsWithinReach(0, 0, 22, 22, 30));
        }

        [Test]
        public void ReachIsInclusiveAtTheBoundaryAndRejectsNegatives()
        {
            Assert.IsTrue(NoBreakZoneRange.IsWithinReach(0, 0, 0, 0, 0));
            Assert.IsFalse(NoBreakZoneRange.IsWithinReach(0, 0, 1, 0, 0));
            Assert.IsFalse(NoBreakZoneRange.IsWithinReach(0, 0, 0, 0, -1));
        }

        [Test]
        public void ReachIsMeasuredFromWhereverThePlayerStands()
        {
            Assert.IsTrue(NoBreakZoneRange.IsWithinReach(-1000, 500, -1030, 500, 30));
            Assert.IsFalse(NoBreakZoneRange.IsWithinReach(-1000, 500, -1031, 500, 30));
        }

    }
}
