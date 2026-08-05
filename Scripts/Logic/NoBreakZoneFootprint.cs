// Which tiles an object actually stands on.
//
// 기획서 §6 judges a multi-tile object by all of them, so knowing the footprint is a prerequisite
// for judging anything larger than one tile. Up to now the protection system used the object's
// origin tile alone, which is the same answer for a 1×1 and the wrong one for everything else.
//
// The arithmetic is the game's own, from ck-db Pug.Other/DetectRoomSystem.cs:167-192, where it
// counts the tiles an object blocks:
//
//     tile += prefabCornerOffset;
//     occupied = [tile, tile + prefabTileSize)
//
// Reading prefabTileSize out of the object database and applying DirectionCD for rotated objects
// needs game types, so that part stays in the system. This holds the part that does not, which is
// also the part with the off-by-one in it.
public static class NoBreakZoneFootprint
{
    /// Turns an object's origin tile and its prefab size/offset into an INCLUSIVE tile rectangle.
    ///
    /// A size of zero or less is treated as 1×1. That case means the object database had nothing to
    /// say about this object, and falling back to one tile keeps it protectable — silently dropping
    /// it out of protection would look exactly like the pylon not working.
    public static void Rect(
        int originX, int originZ,
        int sizeX, int sizeZ,
        int cornerOffsetX, int cornerOffsetZ,
        out int minX, out int minZ, out int maxX, out int maxZ)
    {
        if (sizeX < 1)
        {
            sizeX = 1;
        }

        if (sizeZ < 1)
        {
            sizeZ = 1;
        }

        minX = originX + cornerOffsetX;
        minZ = originZ + cornerOffsetZ;

        // The game's range is half-open, [tile, tile + size). Inclusive is friendlier for the
        // caller, hence the -1 — and it is why a size of 0 could not be passed through untouched:
        // it would produce max < min and read as a degenerate rectangle.
        maxX = minX + sizeX - 1;
        maxZ = minZ + sizeZ - 1;
    }
}
