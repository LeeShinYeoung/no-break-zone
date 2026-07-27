using PugMod;
using UnityEngine;

// Mod entry point. Not strictly required for ECS systems to register (Unity auto-discovers them),
// but it gives us a load-time log line so we can confirm in Player.log that the mod's code
// assembly actually loaded during in-game verification.
public class NoBreakZoneMod : IMod
{
    public const string Version = "0.0.1-stage4";

    public void EarlyInit()
    {
    }

    public void Init()
    {
        Debug.Log($"[NoBreakZone] loaded ({Version}) — hardcoded installation protection active");
    }

    public void Shutdown()
    {
    }

    public void ModObjectLoaded(Object obj)
    {
    }

    public void Update()
    {
    }
}
