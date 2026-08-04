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

        [Test]
        public void MultiTileObjectNeedsEveryTileInside()
        {
            // 기획서 §6: "모든 타일이 범위 안에 들어와야 보호된다. 한 칸이라도 밖으로 나가면
            // 보호되지 않는다." A 2×1 workbench straddling the boundary is not protected.
            Assert.IsTrue(NoBreakZoneRange.CoversRect(0, 0, 9, 0, 10, 0, 10));
            Assert.IsFalse(NoBreakZoneRange.CoversRect(0, 0, 10, 0, 11, 0, 10));
        }

        [Test]
        public void SingleTileRectBehavesLikeASingleTile()
        {
            Assert.IsTrue(NoBreakZoneRange.CoversRect(0, 0, 10, 10, 10, 10, 10));
            Assert.IsFalse(NoBreakZoneRange.CoversRect(0, 0, 11, 11, 11, 11, 10));
        }

        [Test]
        public void InvertedRectIsRejectedRatherThanSilentlyAccepted()
        {
            Assert.IsFalse(NoBreakZoneRange.CoversRect(0, 0, 5, 0, 4, 0, 10));
            Assert.IsFalse(NoBreakZoneRange.CoversRect(0, 0, 0, 5, 0, 4, 10));
        }
    }
}
