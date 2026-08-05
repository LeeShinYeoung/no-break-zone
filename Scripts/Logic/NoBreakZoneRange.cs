// Pure geometry for pylon protection ranges. Deliberately free of any game or Unity type so it can
// be unit tested without loading the game assemblies (Editor/Tests/NoBreakZoneRangeTests.cs).
//
// The world is the XZ plane and tile coordinates come from LocalTransform.Position.RoundToInt2(),
// which maps float3 -> int2(round(x), round(z)) (Pug.UnityExtensions.ExtensionMethods:551). Callers
// pass those two ints straight through, so "z" here is the int2's y.
//
// Shape is a SQUARE, not a circle (기획서 §6): a tile is covered when the larger of |dx| and |dz| is
// within the radius. Diameter N=21 means radius 10 — ten tiles in every direction plus the pylon's
// own tile.
public static class NoBreakZoneRange
{
    // 기획서 §6. Configurable later via Conf/ (7단계); the constant is the default, not a hard limit.
    public const int DefaultDiameter = 21;

    // A diameter is always odd because the pylon occupies the centre tile. An even number rounds
    // down so that 21 and 22 both mean "10 tiles out", rather than silently shifting the centre.
    public static int RadiusFromDiameter(int diameter)
    {
        if (diameter < 1)
        {
            return 0;
        }

        return (diameter - 1) / 2;
    }

    // Chebyshev distance: the square's edge, not Euclidean.
    public static bool Covers(int pylonX, int pylonZ, int tileX, int tileZ, int radius)
    {
        if (radius < 0)
        {
            return false;
        }

        int dx = tileX - pylonX;
        int dz = tileZ - pylonZ;

        if (dx < 0)
        {
            dx = -dx;
        }

        if (dz < 0)
        {
            dz = -dz;
        }

        return dx <= radius && dz <= radius;
    }

    // 기획서 §4: the remote reaches 30 tiles. Configurable later via Conf/ (7단계).
    //
    // Deliberately a fixed tile count rather than "whatever is on screen": 기획서 §4 argues that a
    // screen-relative reach means "모니터가 큰 사람이 유리해지고, 멀티플레이에서는 플레이어마다
    // 사거리가 달라진다". The lens overlay follows the same rule for the same reason.
    public const int DefaultRemoteReach = 30;

    // Round, not square. The protection area is a square because a base is (기획서 §6), but reach
    // is about how far a player can act, and a square there would quietly give 41% more reach on
    // the diagonal than along an axis.
    public static bool IsWithinReach(int fromX, int fromZ, int toX, int toZ, int reach)
    {
        if (reach < 0)
        {
            return false;
        }

        long dx = toX - fromX;
        long dz = toZ - fromZ;
        return dx * dx + dz * dz <= (long)reach * reach;
    }

    // 기획서 §6, both halves of it at once.
    //
    //   "설치물이 여러 타일을 차지하는 경우, 모든 타일이 범위 안에 들어와야 보호된다."
    //   "범위가 겹쳐도 문제없다. 어느 하나의 켜진 파일런 범위 안에 있으면 보호된다."
    //
    // Read together: every occupied tile has to be covered, but each tile may be covered by a
    // DIFFERENT pylon. A workbench lying across the seam between two overlapping pylons is
    // protected, which is what "겹쳐도 문제없다" leads a player to expect. With one pylon this is
    // the same answer as testing the rectangle's two extreme corners against it.
    //
    // Both ranges are inclusive. Pylon coordinates come in as parallel arrays because the caller
    // reuses one buffer per frame rather than allocating.
    public static bool AllTilesCovered(
        int[] pylonX, int[] pylonZ, int pylonCount,
        int minX, int minZ, int maxX, int maxZ, int radius)
    {
        if (pylonX == null || pylonZ == null || pylonCount <= 0)
        {
            return false;
        }

        if (minX > maxX || minZ > maxZ)
        {
            return false;  // a degenerate footprint protects nothing
        }

        int available = pylonX.Length < pylonZ.Length ? pylonX.Length : pylonZ.Length;
        if (pylonCount > available)
        {
            pylonCount = available;
        }

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!IsCoveredByAny(pylonX, pylonZ, pylonCount, x, z, radius))
                {
                    return false;  // 기획서 §6: "한 칸이라도 밖으로 나가면 보호되지 않는다"
                }
            }
        }

        return true;
    }

    // Private on purpose: AllTilesCovered is the rule 기획서 §6 states, and a second public entry
    // point that answers for one tile invites callers to re-implement the multi-tile rule badly.
    // That is how CoversRect ended up written, tested and never called.
    private static bool IsCoveredByAny(
        int[] pylonX, int[] pylonZ, int pylonCount, int tileX, int tileZ, int radius)
    {
        for (int i = 0; i < pylonCount; i++)
        {
            if (Covers(pylonX[i], pylonZ[i], tileX, tileZ, radius))
            {
                return true;
            }
        }

        return false;
    }
}
