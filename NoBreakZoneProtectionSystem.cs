using Unity.Collections;
using Unity.Entities;

// STAGE 4 — hardcoded damage-block verification (기획서 13장 1단계).
// Makes ALL placed, damageable installations indestructible. No pylon and no range yet:
// this blanket-protects every placeable prefab that can take damage, so we can verify in-game
// that the mechanism actually stops destruction before layering coordinates/pylons on top.
//
// Mechanism (from decompiled Pug.Other.dll):
//   All damage — combat, explosions, tile mining, automation drills — funnels a HealthChange
//   into the shared HealthChangeBuffer. The SINGLE destruction gate is SetEntitiesDestroyedSystem:
//   when health <= 0 it enables EntityDestroyedCD UNLESS the entity carries
//   DontDestroyOnZeroHealthCD{ disabled = false }, in which case it returns early and the entity
//   survives (its health may sit at 0, but it is never destroyed). So adding that one component
//   protects against every damage source at once, at the data level — no Burst patching needed.
//
// RESOURCE-DUPLICATION SAFETY (기획서 6장 "절대 발생해서는 안 되는 것"):
//   Terrain, walls, ore boulders and (some) crops are destroyed through the SAME gate. If we
//   protected anything with HealthCD, drills/pickaxes would drive ore to 0 health but it would
//   never deplete — infinite resources. We therefore EXCLUDE TileCD / MineableCD / DiggableCD and
//   only target PlaceablePrefab installations that are explicitly DamageableObjectCD.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class NoBreakZoneProtectionSystem : PugSimulationSystemBase
{
    // Placeable, damageable installations that are NOT terrain/ore/crops and not yet protected.
    // Once an entity is protected it gains DontDestroyOnZeroHealthCD and leaves this query, so the
    // steady-state cost is ~0 — only newly placed objects are ever processed.
    private EntityQuery _unprotectedInstallations;

    protected override void OnCreate()
    {
        base.OnCreate();

        _unprotectedInstallations = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<HealthCD>(),
                ComponentType.ReadOnly<DamageableObjectCD>(),
                ComponentType.ReadOnly<ObjectTypeCD>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<TileCD>(),
                ComponentType.ReadOnly<MineableCD>(),
                ComponentType.ReadOnly<DiggableCD>(),
                ComponentType.ReadOnly<DontDestroyOnZeroHealthCD>(),
            },
        });
    }

    protected override void OnUpdate()
    {
        if (!_unprotectedInstallations.IsEmpty)
        {
            // Snapshot before mutating: AddComponentData is a structural change, but these copies
            // are taken first so iterating them stays valid.
            var entities = _unprotectedInstallations.ToEntityArray(Allocator.Temp);
            var objectTypes = _unprotectedInstallations.ToComponentDataArray<ObjectTypeCD>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                // Only player-placeable installations (chests, workbenches, lamps, drills, ...).
                if (objectTypes[i].Value != ObjectType.PlaceablePrefab)
                {
                    continue;
                }

                EntityManager.AddComponentData(entities[i], new DontDestroyOnZeroHealthCD { disabled = false });
            }

            entities.Dispose();
            objectTypes.Dispose();
        }

        base.OnUpdate();
    }
}
