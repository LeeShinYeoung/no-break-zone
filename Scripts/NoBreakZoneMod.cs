using PugMod;
using UnityEngine;

// Mod entry point. Not strictly required for ECS systems to register (Unity auto-discovers them),
// but it gives us a load-time log line so we can confirm in Player.log that the mod's code
// assembly actually loaded during in-game verification.
public class NoBreakZoneMod : IMod
{
    // First public release. Earlier values carried a 기획서 13장 stage suffix ("0.0.1-stage7"),
    // which said where the work was rather than what a player has; a published mod should answer
    // the second question.
    public const string Version = "1.0.0";

    public void EarlyInit()
    {
    }

    public void Init()
    {
        NoBreakZoneConfig.Register();
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
        KeepDedicatedServerAwake();
    }

    // ONLY WHEN selfTest IS ON, WHICH IS NEVER FOR A PLAYER.
    //
    // A dedicated server with nobody connected pauses itself: ECSManager.Pause sets the server
    // world's SimulationSystemGroup.Enabled to false and Time.timeScale to 0. That is correct for a
    // real server and fatal for an unattended check, because the mod's systems are in that group —
    // the self test would sit at step 0 forever.
    //
    // This hook is the one piece of the mod that keeps running while the group is disabled: IMod.Update
    // is driven by PugMod's own component, outside the ECS player loop. Asking the game to resume
    // every frame is enough to keep the simulation stepping, and it costs a null check in normal play.
    private static void KeepDedicatedServerAwake()
    {
        if (!NoBreakZoneConfig.SelfTest)
        {
            return;
        }

        try
        {
            Manager.ecs?.Resume();
        }
        catch (System.Exception e)
        {
            // Never take the mod down over a diagnostic convenience.
            Debug.LogWarning($"[NBZTEST] could not resume the simulation: {e.Message}");
        }
    }
}
