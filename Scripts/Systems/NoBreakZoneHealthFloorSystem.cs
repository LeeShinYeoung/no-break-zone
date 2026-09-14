using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

// Makes protection mean protection, rather than destruction deferred until a switch is flipped.
//
// THE BUG THIS EXISTS FOR. NoBreakZoneProtectionSystem protects by blocking destruction, not by
// preventing damage — that was the right call: every damage source in the game converges on one
// destroy gate, so one component covers all of them. What it does not cover is the damage itself.
// The game clamps health rather than refusing the hit:
//
//     healthCD.health = math.clamp(healthCD.health + num, 0, healthCD.maxHealth);   // Update...
//     if ((hasDontDestroy && !disabled) || health > 0) return;                      // SetEntities...
//
// So mining a protected wall drives it to zero health, where DontDestroyOnZeroHealthCD holds it up.
// The wall is not safe. It is one component away from dead, and switching the pylon off removes
// exactly that component — which is why a player who mined inside their own base and then switched
// off watched the whole base turn into items in a single frame (2026-09-09).
//
// WHY A HEALTH FLOOR AND NOT A DAMAGE FILTER. The obvious alternative is to drop our entries out of
// HealthChangeBuffer before they land. That works only as long as we know every producer, and it
// fails silently the day a new one appears. This runs at the last moment before the gate and does
// not care where the damage came from: anything that wants to destroy something has to pass
// SetEntitiesDestroyedSystem, and by then the condition it tests is already false.
//
// RESTORED TO FULL, NOT TO ONE. Leaving a protected thing at 1 HP is the same bug in miniature — a
// base sitting at one health is a base that a single explosion levels the moment the pylon goes off.
// design.md §6's promise to the player is "my base is protected", not "my base barely holds on".
//
// ONLY WHAT WE PROTECTED. NoBreakZoneProtectedCD is the same boundary Release() respects: things the
// game itself made indestructible, and things we decided against, are none of our business.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
[UpdateAfter(typeof(UpdateHealthSystemGroup))]
[UpdateBefore(typeof(SetEntitiesDestroyedSystem))]
public partial class NoBreakZoneHealthFloorSystem : SystemBase
{
    private EntityQuery _protectedWithHealth;

    // Logged once rather than per frame: a protected base under fire would otherwise fill the log,
    // and the interesting fact is that this fires at all, not how often.
    private bool _announced;

    protected override void OnCreate()
    {
        // Both worlds, for the same reason the protection system runs in both: the client predicts
        // destruction too, so a floor applied only on the server would let the client show the base
        // collapsing and then snap it back.
        _protectedWithHealth = GetEntityQuery(
            ComponentType.ReadOnly<NoBreakZoneProtectedCD>(),
            ComponentType.ReadWrite<HealthCD>());
    }

    protected override void OnUpdate()
    {
        if (_protectedWithHealth.IsEmpty)
        {
            return;
        }

        var entities = _protectedWithHealth.ToEntityArray(Allocator.Temp);
        var em = EntityManager;
        int restored = 0;

        for (int i = 0; i < entities.Length; i++)
        {
            // NEVER FEED SOMETHING THAT PAYS OUT WHEN HIT. DropsLootWhenDamagedCD marks the objects
            // that yield every time they are damaged rather than when they die — a drill's ore
            // boulder above all. Restoring one to full each frame would remove the only brake the
            // game has on it: the health pipeline skips entities already at zero
            // (`if (healthCD.health <= 0) continue;`), so a depleted boulder simply stops paying.
            // With a floor under it the loop becomes hit, pay, restore, hit — the resource
            // duplication design.md §6 puts above every other property of this mod.
            //
            // NoBreakZoneProtectionRule already refuses to protect these, so this should be
            // unreachable. It is here because "unreachable" is a claim about code that keeps
            // changing, and the cost of being wrong is a save nobody can repair.
            if (em.HasComponent<DropsLootWhenDamagedCD>(entities[i]))
            {
                continue;
            }

            HealthCD health = em.GetComponentData<HealthCD>(entities[i]);
            if (health.health > 0)
            {
                continue;
            }

            health.health = health.maxHealth;
            em.SetComponentData(entities[i], health);
            restored++;
        }

        entities.Dispose();

        if (restored > 0 && !_announced)
        {
            _announced = true;
            Debug.Log($"[NoBreakZone] healing protected objects back from zero — the first of "
                      + $"{restored} this frame (world={World.Name})");
        }
    }
}
