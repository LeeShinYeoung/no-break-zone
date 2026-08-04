using System.Collections.Generic;
using Pug.UnityExtensions;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// STAGE 2 — protection scoped to a square around a pylon (기획서 13장 2단계).
//
// 1단계 shipped an unconditional version of this: every placeable in the world became
// indestructible. That was the point — it proved the mechanism. What it could not do is let anyone
// ever remodel their base. This narrows it to "inside a pylon's square", with the pylon coordinates
// still hardcoded because the pylon object itself does not exist until 3단계.
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
    // 2단계 scaffold: one pylon at the world origin. 3단계 replaces this with the real positions
    // published by NoBreakZonePylonRegistrySystem, and this array goes away.
    private static readonly int2[] HardcodedPylons =
    {
        new int2(0, 0),
    };

    private EntityQuery _candidates;
    private readonly HashSet<ObjectID> _logged = new HashSet<ObjectID>();

    protected override void OnCreate()
    {
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
            },
        });
    }

    protected override void OnUpdate()
    {
        if (_candidates.IsEmpty)
        {
            return;
        }

        // Copy first: adding components below is a structural change that would invalidate live
        // chunk iteration.
        var entities = _candidates.ToEntityArray(Allocator.Temp);
        var objectTypes = _candidates.ToComponentDataArray<ObjectTypeCD>(Allocator.Temp);
        var objectDatas = _candidates.ToComponentDataArray<ObjectDataCD>(Allocator.Temp);
        var transforms = _candidates.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var em = EntityManager;
        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneRange.DefaultDiameter);

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
            if (!IsInsideAnyPylon(tile, radius))
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

    // 기획서 §6: overlapping squares are fine — being inside any one active pylon is enough.
    private static bool IsInsideAnyPylon(int2 tile, int radius)
    {
        for (int i = 0; i < HardcodedPylons.Length; i++)
        {
            int2 pylon = HardcodedPylons[i];
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
