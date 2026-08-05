using PugMod;
using UnityEngine;

// Mod entry point. Not strictly required for ECS systems to register (Unity auto-discovers them),
// but it gives us a load-time log line so we can confirm in Player.log that the mod's code
// assembly actually loaded during in-game verification.
public class NoBreakZoneMod : IMod
{
    // The suffix tracks 기획서 13장 stage numbering, which is not the repo's old numbering — the
    // previous value said "stage4" under that older scheme (status.md explains the renumbering).
    public const string Version = "0.0.1-stage6-complete";

    public void EarlyInit()
    {
    }

    public void Init()
    {
        Debug.Log($"[NoBreakZone] loaded ({Version}) — protection follows switched-on {NoBreakZonePylonRegistrySystem.PylonObjectName}");
    }

    public void Shutdown()
    {
        NoBreakZoneRangeOverlay.Dispose();
    }

    // Every asset in the mod's bundle arrives here. It is the only way to get hold of one at
    // runtime — a mod never sees a path or a guid — so the lens's marker sprite is picked out by
    // name as it goes past.
    public void ModObjectLoaded(Object obj)
    {
        NoBreakZoneRangeOverlay.RegisterLoadedObject(obj);
    }

    // Client-side per-frame hook. The range overlay lives here rather than in an ECS system so it
    // cannot end up running in the server simulation (design.md §9).
    public void Update()
    {
        NoBreakZoneRangeOverlay.Update();
        NoBreakZoneRemoteFeedback.Update();
    }
}
