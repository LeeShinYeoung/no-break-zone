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

    // Number of tiles on the square's edge — what the lens draws (기획서 §7). Radius 10 gives 80:
    // a 21-wide ring, counted once per tile, corners included exactly once.
    public static int BoundaryTileCount(int radius)
    {
        if (radius < 0)
        {
            return 0;
        }

        if (radius == 0)
        {
            return 1;  // the pylon's own tile is the whole square, so it is also the whole edge
        }

        int side = radius * 2 + 1;
        return side * 4 - 4;  // four sides, minus the corners each pair shares
    }

    // Walks the edge tiles of the square in order, writing into the caller's arrays so the overlay
    // can reuse one buffer every frame instead of allocating 80 int2s per pylon.
    //
    // Returns how many were written. Writing stops if the arrays are shorter than
    // BoundaryTileCount, so a caller that under-sizes gets a partial ring rather than a crash.
    public static int WriteBoundaryTiles(
        int pylonX, int pylonZ, int radius, int[] tileX, int[] tileZ)
    {
        if (radius < 0 || tileX == null || tileZ == null)
        {
            return 0;
        }

        int capacity = tileX.Length < tileZ.Length ? tileX.Length : tileZ.Length;
        int written = 0;

        if (radius == 0)
        {
            if (capacity > 0)
            {
                tileX[0] = pylonX;
                tileZ[0] = pylonZ;
                written = 1;
            }

            return written;
        }

        // Top and bottom rows in full, then the left and right columns without their end tiles —
        // that is what keeps each corner appearing exactly once.
        for (int dx = -radius; dx <= radius && written < capacity; dx++)
        {
            tileX[written] = pylonX + dx;
            tileZ[written] = pylonZ + radius;
            written++;
        }

        for (int dx = -radius; dx <= radius && written < capacity; dx++)
        {
            tileX[written] = pylonX + dx;
            tileZ[written] = pylonZ - radius;
            written++;
        }

        for (int dz = -radius + 1; dz <= radius - 1 && written < capacity; dz++)
        {
            tileX[written] = pylonX - radius;
            tileZ[written] = pylonZ + dz;
            written++;

            if (written >= capacity)
            {
                break;
            }

            tileX[written] = pylonX + radius;
            tileZ[written] = pylonZ + dz;
            written++;
        }

        return written;
    }

    // Multi-tile objects are protected only when EVERY occupied tile is inside the square
    // (기획서 §6: "모든 타일이 범위 안에 들어와야 보호된다"). Both ranges are inclusive.
    // Because the range is itself a rectangle, testing the two extreme corners covers all tiles.
    public static bool CoversRect(
        int pylonX, int pylonZ, int minX, int minZ, int maxX, int maxZ, int radius)
    {
        if (minX > maxX || minZ > maxZ)
        {
            return false;
        }

        return Covers(pylonX, pylonZ, minX, minZ, radius)
               && Covers(pylonX, pylonZ, maxX, maxZ, radius);
    }
}
