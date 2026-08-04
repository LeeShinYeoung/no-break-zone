using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

// STAGE 4 — hardcoded damage-block verification (기획서 13장 1단계). v5 — the flicker fix.
//
// WHY v1–v4 flickered (invisible object + endless phantom loot), from decompiling
// PlayerAttackSystem / PlayerController / GhostUpdateSystem:
//   * Mining/attack/explosion damage runs PREDICTED on BOTH the client and the server world
//     (PlayerAttackSystem: ServerSimulation|ClientSimulation, PredictedSimulationSystemGroup).
//   * That predicted object-damage path (PlayerController.DealDamageToObject) checks ONLY
//     IndestructibleCD (and tile-immune). It does NOT consult ImmuneToDamageCD or DontDestroy — so our
//     old components never stopped the client from enqueueing damage.
//   * NetCode freezes each ghost's replicated component set at BAKE time. A component we AddComponent at
//     runtime (server-only) is never serialized to clients. So the client kept predicting the object's
//     death (renders destroyed + predicted/phantom loot) while the server kept it alive → per-swing
//     rollback flicker.
//
// FIX: enable the game's own IndestructibleCD. In DealDamageToObject an enabled IndestructibleCD makes the
// hit deal ZERO damage (no HealthChange enqueued) on whichever world evaluates it — so if BOTH worlds have
// it enabled locally, neither predicts any damage → health never drops → nothing to destroy → no flicker.
// Because runtime-added components don't replicate, we run this system in BOTH worlds and enable it on each
// world's local copy. DontDestroyOnZeroHealthCD is added as a cheap backstop for any exotic damage source
// that might bypass DealDamageToObject (SetEntitiesDestroyedSystem also runs predicted on both worlds and
// reads it locally).
//
// Discriminator (validated vs the full DB, Editor/GameData/object_flags.csv): protect PlaceablePrefab +
// HealthCD, excluding tiles (query) and resources/loot-droppers (DestructibleObjectCD /
// DropsLootFromLootTableCD / DropsLootWhenDamagedCD) so ore/pots/walls stay mineable (no resource dup).
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class NoBreakZoneProtectionSystem : SystemBase
{
    private EntityQuery _candidates;
    private readonly HashSet<ObjectID> _logged = new HashSet<ObjectID>();

    protected override void OnCreate()
    {
        // WithNone<IndestructibleCD>: once enabled the entity leaves the query (enableable component),
        // so each object is processed once per world.
        _candidates = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<HealthCD>(),
                ComponentType.ReadOnly<ObjectTypeCD>(),
                ComponentType.ReadOnly<ObjectDataCD>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<TileCD>(),
                ComponentType.ReadOnly<IndestructibleCD>(),
            },
        });
    }

    protected override void OnUpdate()
    {
        if (_candidates.IsEmpty)
        {
            return;
        }

        var entities = _candidates.ToEntityArray(Allocator.Temp);
        var objectTypes = _candidates.ToComponentDataArray<ObjectTypeCD>(Allocator.Temp);
        var objectDatas = _candidates.ToComponentDataArray<ObjectDataCD>(Allocator.Temp);
        var em = EntityManager;

        for (int i = 0; i < entities.Length; i++)
        {
            if (objectTypes[i].Value != ObjectType.PlaceablePrefab)
            {
                continue;
            }

            var entity = entities[i];

            bool isResource = em.HasComponent<DestructibleObjectCD>(entity)
                              || em.HasComponent<DropsLootFromLootTableCD>(entity)
                              || em.HasComponent<DropsLootWhenDamagedCD>(entity);
            if (isResource)
            {
                continue;
            }

            // Primary: game-native indestructibility, checked in the predicted damage path on both worlds.
            em.AddComponent<IndestructibleCD>(entity);
            em.SetComponentEnabled<IndestructibleCD>(entity, true);

            // Backstop: block the destruction gate for any damage that bypasses DealDamageToObject.
            if (!em.HasComponent<DontDestroyOnZeroHealthCD>(entity))
            {
                em.AddComponentData(entity, new DontDestroyOnZeroHealthCD { disabled = false });
            }

            if (_logged.Add(objectDatas[i].objectID))
            {
                Debug.Log($"[NoBreakZone] PROTECT {objectDatas[i].objectID} (world={World.Name})");
            }
        }

        entities.Dispose();
        objectTypes.Dispose();
        objectDatas.Dispose();
    }
}
