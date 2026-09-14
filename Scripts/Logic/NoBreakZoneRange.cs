// Pure geometry for pylon protection ranges. Deliberately free of any game or Unity type so it can
// be unit tested without loading the game assemblies (Editor/LogicTests~/Program.cs).
//
// The world is the XZ plane and tile coordinates come from LocalTransform.Position.RoundToInt2(),
// which maps float3 -> int2(round(x), round(z)) (Pug.UnityExtensions.ExtensionMethods:551). Callers
// pass those two ints straight through, so "z" here is the int2's y.
//
// Shape is a SQUARE, not a circle (design.md §6): a tile is covered when the larger of |dx| and
// |dz| is within the radius. Diameter N=21 means radius 10 — ten tiles in every direction plus the
// pylon's own tile.
public static class NoBreakZoneRange
{
    // design.md §6. Configurable later via Conf/ (stage 7); the constant is the default, not a hard
    // limit.
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

    // design.md §4: the remote reaches 30 tiles. Configurable later via Conf/ (stage 7).
    //
    // Deliberately a fixed tile count rather than "whatever is on screen": design.md §4 argues that
    // a screen-relative reach means "players with bigger monitors gain an advantage, and in
    // multiplayer the reach differs from player to player". The lens overlay follows the same rule
    // for the same reason.
    public const int DefaultRemoteReach = 30;

    // Round, not square. The protection area is a square because a base is (design.md §6), but
    // reach is about how far a player can act, and a square there would quietly give 41% more reach
    // on the diagonal than along an axis.
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

    // design.md §6, both halves of it at once.
    //
    //   "When a placed object occupies several tiles, every one of them must be inside the range
    //    for it to be protected."
    //   "Overlapping ranges are fine. Anything inside the range of any one switched-on pylon is
    //    protected."
    //
    // Read together: every occupied tile has to be covered, but each tile may be covered by a
    // DIFFERENT pylon. A workbench lying across the seam between two overlapping pylons is
    // protected, which is what "overlapping ranges are fine" leads a player to expect. With one
    // pylon this is the same answer as testing the rectangle's two extreme corners against it.
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
                    return false;  // design.md §6: "if even one tile is outside, it is not protected"
                }
            }
        }

        return true;
    }

    // Private on purpose: AllTilesCovered is the rule design.md §6 states, and a second public
    // entry point that answers for one tile invites callers to re-implement the multi-tile rule
    // badly. That is how CoversRect ended up written, tested and never called.
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

    // "Is the set of switched-on pylons still the one protection was judged against?" Asked by
    // NoBreakZonePylonRegistrySystem every frame, and a "no" re-judges the whole world.
    //
    // A SET, NOT A SEQUENCE. The pylon query returns entities in chunk order, and a pylon changes
    // chunk whenever its component set changes: it gains its own two guards the first time it is
    // switched on, and a pylon streaming back in is a new entity altogether. Comparing position by
    // position read each of those as a new set and re-judged the world a second time, doubling the
    // cost of every first switch-on and every reload (verify.ps1 -Perf, 2026-09-14).
    //
    // Tiles are taken to be unique — two pylons cannot stand on one tile — so equal counts plus every
    // tile of one list found in the other is equality. The same-order pass comes first because it is
    // what nearly every frame looks like, and it costs one walk.
    public static bool SameTiles(
        int[] ax, int[] az, int aCount,
        int[] bx, int[] bz, int bCount)
    {
        aCount = ClampCount(ax, az, aCount);
        bCount = ClampCount(bx, bz, bCount);

        if (aCount != bCount)
        {
            return false;
        }

        bool sameOrder = true;
        for (int i = 0; i < aCount; i++)
        {
            if (ax[i] != bx[i] || az[i] != bz[i])
            {
                sameOrder = false;
                break;
            }
        }

        if (sameOrder)
        {
            return true;
        }

        for (int i = 0; i < aCount; i++)
        {
            if (!ContainsTile(bx, bz, bCount, ax[i], az[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsTile(int[] xs, int[] zs, int count, int x, int z)
    {
        for (int i = 0; i < count; i++)
        {
            if (xs[i] == x && zs[i] == z)
            {
                return true;
            }
        }

        return false;
    }

    // A count beyond the buffer is clamped rather than read past the end, and a missing buffer holds
    // nothing: the same reading AllTilesCovered gives its input.
    private static int ClampCount(int[] xs, int[] zs, int count)
    {
        if (xs == null || zs == null || count <= 0)
        {
            return 0;
        }

        int available = xs.Length < zs.Length ? xs.Length : zs.Length;
        return count > available ? available : count;
    }
}
