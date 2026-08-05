using System.Collections.Generic;
using Pug.UnityExtensions;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// STAGE 2/3 — protection scoped to a square around a pylon (기획서 13장 2·3단계).
//
// 1단계 shipped an unconditional version of this: every placeable in the world became
// indestructible. That was the point — it proved the mechanism. What it could not do is let anyone
// ever remodel their base. 2단계 narrowed it to "inside a pylon's square" against a hardcoded
// coordinate; 3단계 replaced that constant with the real placed pylons, which
// NoBreakZonePylonRegistrySystem publishes just before this system runs.
//
// HOW PROTECTION WORKS (carried over from 1단계, verified in game — research.md 9장):
// Mining/attack/explosion damage runs PREDICTED on BOTH the client and the server world, and that
// path (PlayerController.DealDamageToObject) consults ONLY IndestructibleCD. A component added at
// runtime on the server is never replicated to clients, so a server-only fix left the client
// predicting the object's death every swing — invisible chest, phantom loot. The fix is to enable
// the game's own IndestructibleCD in BOTH worlds, so neither predicts any damage at all.
// DontDestroyOnZeroHealthCD is kept as a backstop for damage that bypasses DealDamageToObject.
//
// WHY A TAG INSTEAD OF A PER-FRAME SWEEP: 기획서 §9 forbids walking the world every frame. Entities
// leave the query permanently once judged (NoBreakZoneEvaluatedCD), so steady-state cost is
// proportional to newly streamed-in entities, not to base size.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class NoBreakZoneProtectionSystem : SystemBase
{
    private EntityQuery _candidates;
    private EntityQuery _ours;
    private NoBreakZonePylonRegistrySystem _registry;
    private readonly HashSet<ObjectID> _logged = new HashSet<ObjectID>();

    protected override void OnCreate()
    {
        // Both systems are managed and run in order on the main thread, so reading the registry's
        // published positions during OnUpdate needs no copy or job dependency.
        _registry = World.GetOrCreateSystemManaged<NoBreakZonePylonRegistrySystem>();

        // LocalTransform is required now: it is how the game itself resolves an entity to a tile
        // (Position.RoundToInt2(), see DetectRoomSystem). Anything without one has no place on the
        // grid and cannot be inside a square.
        _candidates = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<HealthCD>(),
                ComponentType.ReadOnly<ObjectTypeCD>(),
                ComponentType.ReadOnly<ObjectDataCD>(),
                ComponentType.ReadOnly<LocalTransform>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<TileCD>(),
                ComponentType.ReadOnly<NoBreakZoneEvaluatedCD>(),

                // A pylon stands inside its own square, so without this it would protect itself.
                // 기획서 §6 does want that eventually ("켜져 있는 동안 파일런은 무적"), but the same
                // sentence continues "회수하려면 먼저 꺼야 한다" — and nothing can switch a pylon off
                // until 4단계. Self-protecting it now would mean a pylon placed during 체크포인트 1
                // could never be picked up again. 4단계 grants the pylon IndestructibleCD directly,
                // tied to its variation, rather than through this discriminator.
                ComponentType.ReadOnly<NoBreakZonePylonCD>(),
            },
        });

        // Everything this mod made indestructible, and nothing else. Switching a pylon off has to
        // give these back — see ReleaseUncovered.
        _ours = GetEntityQuery(
            ComponentType.ReadOnly<NoBreakZoneProtectedCD>(),
            ComponentType.ReadOnly<LocalTransform>());
    }

    protected override void OnUpdate()
    {
        var pylons = _registry.Positions;
        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneRange.DefaultDiameter);

        // Before judging anything new: if the set of switched-on pylons just changed, hand back
        // whatever fell outside it. This has to run before the early exits below — switching the
        // last pylon off leaves no squares and no new candidates, and is exactly the case where
        // releasing matters most.
        if (_registry.ConsumeReleaseRequest())
        {
            ReleaseUncovered(pylons, radius);
        }

        if (_candidates.IsEmpty)
        {
            return;
        }

        if (pylons.Length == 0)
        {
            // No switched-on pylon means no square, so nothing can qualify (기획서 §6). Leaving
            // early also leaves the candidates untagged, so they get judged properly once one is
            // switched on rather than being written off now.
            return;
        }

        // Copy first: adding components below is a structural change that would invalidate live
        // chunk iteration.
        var entities = _candidates.ToEntityArray(Allocator.Temp);
        var objectTypes = _candidates.ToComponentDataArray<ObjectTypeCD>(Allocator.Temp);
        var objectDatas = _candidates.ToComponentDataArray<ObjectDataCD>(Allocator.Temp);
        var transforms = _candidates.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var em = EntityManager;

        for (int i = 0; i < entities.Length; i++)
        {
            var entity = entities[i];

            // Mark first, and unconditionally: an entity we decided not to protect must not come
            // back through the query next frame.
            em.AddComponent<NoBreakZoneEvaluatedCD>(entity);

            bool qualifies = NoBreakZoneProtectionRule.ShouldProtect(
                (int)objectTypes[i].Value,
                true, // HealthCD is in the query's All list
                false, // TileCD is in the query's None list
                em.HasComponent<DestructibleObjectCD>(entity),
                em.HasComponent<DropsLootFromLootTableCD>(entity),
                em.HasComponent<DropsLootWhenDamagedCD>(entity));

            if (!qualifies)
            {
                continue;
            }

            int2 tile = transforms[i].Position.RoundToInt2();
            if (!IsInsideAnyPylon(pylons, tile, radius))
            {
                continue;
            }

            Protect(em, entity, objectDatas[i].objectID);
        }

        entities.Dispose();
        objectTypes.Dispose();
        objectDatas.Dispose();
        transforms.Dispose();
    }

    // STAGE 4 — the other half of the switch (기획서 §6: "기지를 수정하려면 파일런을 끄면 된다").
    //
    // Up to 3단계 this system only ever added protection, which made the toggle pointless: turning a
    // pylon off left every chest around it just as indestructible as before. This gives them back.
    //
    // ONLY ENTITIES CARRYING NoBreakZoneProtectedCD ARE TOUCHED, and that tag is only ever applied
    // to something that was destructible when we found it (see Protect). Objects the game ships
    // indestructible — the Core, boss-arena scenery — never receive it, so no amount of pylon
    // switching can make them breakable. That is the whole reason the tag exists.
    //
    // An object still covered by some other switched-on pylon keeps everything, so overlapping
    // squares behave the way 기획서 §6 describes when only one of them is switched off.
    private void ReleaseUncovered(NativeArray<int2> pylons, int radius)
    {
        if (_ours.IsEmpty)
        {
            return;
        }

        var entities = _ours.ToEntityArray(Allocator.Temp);
        var transforms = _ours.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var em = EntityManager;
        int released = 0;

        for (int i = 0; i < entities.Length; i++)
        {
            int2 tile = transforms[i].Position.RoundToInt2();
            if (IsInsideAnyPylon(pylons, tile, radius))
            {
                continue;
            }

            Release(em, entities[i]);
            released++;
        }

        entities.Dispose();
        transforms.Dispose();

        if (released > 0)
        {
            Debug.Log($"[NoBreakZone] released {released} object(s) (world={World.Name})");
        }
    }

    private static void Release(EntityManager em, Entity entity)
    {
        // Disable rather than remove: NetCode fixes a ghost's component set at bake time, and the
        // enable flag is the part the damage path actually reads (research.md 9장).
        if (em.HasComponent<IndestructibleCD>(entity))
        {
            em.SetComponentEnabled<IndestructibleCD>(entity, false);
        }

        if (em.HasComponent<DontDestroyOnZeroHealthCD>(entity))
        {
            em.SetComponentData(entity, new DontDestroyOnZeroHealthCD { disabled = true });
        }

        // Dropping the claim last, so a mid-way failure leaves the object still marked as ours and
        // therefore still releasable, rather than stranded as indestructible with nobody owning it.
        em.RemoveComponent<NoBreakZoneProtectedCD>(entity);
    }

    // 기획서 §6: overlapping squares are fine — being inside any one active pylon is enough.
    private static bool IsInsideAnyPylon(NativeArray<int2> pylons, int2 tile, int radius)
    {
        for (int i = 0; i < pylons.Length; i++)
        {
            int2 pylon = pylons[i];
            if (NoBreakZoneRange.Covers(pylon.x, pylon.y, tile.x, tile.y, radius))
            {
                return true;
            }
        }

        return false;
    }

    private void Protect(EntityManager em, Entity entity, ObjectID objectID)
    {
        // Leave objects that were already indestructible alone, and do not claim them as ours —
        // otherwise 4단계 would "restore" them to destructible when a pylon switches off.
        bool alreadyNativelyIndestructible =
            em.HasComponent<IndestructibleCD>(entity)
            && em.IsComponentEnabled<IndestructibleCD>(entity);

        if (!alreadyNativelyIndestructible)
        {
            em.AddComponent<IndestructibleCD>(entity);
            em.SetComponentEnabled<IndestructibleCD>(entity, true);
            em.AddComponent<NoBreakZoneProtectedCD>(entity);
        }

        if (!em.HasComponent<DontDestroyOnZeroHealthCD>(entity))
        {
            em.AddComponentData(entity, new DontDestroyOnZeroHealthCD { disabled = false });
        }

        if (_logged.Add(objectID))
        {
            Debug.Log($"[NoBreakZone] PROTECT {objectID} (world={World.Name})");
        }
    }
}
