// The object names design.md §4 fixed, in one place.
//
// These strings are written into save files, so changing one silently breaks every world that has
// the object placed (CLAUDE.md §5). They also have to match ObjectAuthoring.objectName in the
// prefabs, which Editor/genassets.py writes from its own SPECS list — the two are kept in step by
// hand, so treat both as the same decision rather than two settings.
public static class NoBreakZoneObjectNames
{
    public const string Pylon = "NoBreakZone.Pylon";
    public const string Workbench = "NoBreakZone.Workbench";
    public const string Lens = "NoBreakZone.Lens";
    public const string Remote = "NoBreakZone.Remote";
}
