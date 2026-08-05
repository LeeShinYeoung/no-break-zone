using PugMod;
using Pug.UnityExtensions;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// STAGE 3 — where the protected squares actually come from (기획서 13장 3단계).
//
// 2단계 shipped with the pylon position hardcoded to the world origin, because the pylon object did
// not exist yet. It does now (Prefabs/NoBreakZonePylon.prefab), so this system finds the real ones
// and publishes their tile coordinates for NoBreakZoneProtectionSystem to read.
//
// HOW A PYLON IS RECOGNISED: by ObjectDataCD.objectID, compared against the id the game hands out
// for the name baked into ObjectAuthoring. API.Authoring.GetObjectID is a plain dictionary lookup
// over registered mod objects (ck-db Pug.Other/PugMod/ModAPIAuthoring.cs:28) and returns
// ObjectID.None for a name it does not know, which is also what it returns before the object
// database has finished loading — so nothing is classified until the lookup succeeds.
//
// WHY A TAG INSTEAD OF A PER-FRAME SWEEP (기획서 §9, same reasoning as the protection system):
// deciding "is this a pylon" is a one-time answer per entity, so entities leave the discovery query
// permanently once asked. The recurring per-frame work is then proportional to the number of
// pylons, not to the number of objects in the world.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(NoBreakZoneProtectionSystem))]
public partial class NoBreakZonePylonRegistrySystem : SystemBase
{
    public const string PylonObjectName = NoBreakZoneObjectNames.Pylon;

    private EntityQuery _unclassified;
    private EntityQuery _pylons;
    private EntityQuery _evaluated;

    private ObjectID _pylonObjectID = ObjectID.None;
    private NativeList<int2> _positions;
    private bool _announced;

    /// Tile coordinates of every live pylon. Valid for the rest of the frame once this system has
    /// run; NoBreakZoneProtectionSystem is ordered after it and reads this directly.
    public NativeArray<int2> Positions => _positions.AsArray();

    protected override void OnCreate()
    {
        _positions = new NativeList<int2>(8, Allocator.Persistent);

        // Anything with an id and a place on the grid is a candidate until proven otherwise.
        // TileCD is excluded for the same reason the protection system excludes it: floor tiles are
        // numerous and can never be a pylon.
        _unclassified = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<ObjectDataCD>(),
                ComponentType.ReadOnly<LocalTransform>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<TileCD>(),
                ComponentType.ReadOnly<NoBreakZonePylonScannedCD>(),
            },
        });

        _pylons = GetEntityQuery(
            ComponentType.ReadOnly<NoBreakZonePylonCD>(),
            ComponentType.ReadOnly<LocalTransform>());

        _evaluated = GetEntityQuery(ComponentType.ReadOnly<NoBreakZoneEvaluatedCD>());
    }

    protected override void OnDestroy()
    {
        if (_positions.IsCreated)
        {
            _positions.Dispose();
        }
    }

    protected override void OnUpdate()
    {
        if (!TryResolvePylonObjectID())
        {
            // The object database is not up yet. Classifying now would mark every entity in the
            // world as "not a pylon" and the tag would keep it out of the query forever.
            return;
        }

        Classify();
        CollectPositions();
    }

    private bool TryResolvePylonObjectID()
    {
        if (_pylonObjectID != ObjectID.None)
        {
            return true;
        }

        _pylonObjectID = API.Authoring.GetObjectID(PylonObjectName);
        if (_pylonObjectID == ObjectID.None)
        {
            return false;
        }

        if (!_announced)
        {
            _announced = true;
            Debug.Log($"[NoBreakZone] pylon object id = {(int)_pylonObjectID} (world={World.Name})");
        }

        return true;
    }

    private void Classify()
    {
        if (_unclassified.IsEmpty)
        {
            return;
        }

        // Copy before touching anything: adding a component is a structural change and would
        // invalidate iteration over live chunks.
        var entities = _unclassified.ToEntityArray(Allocator.Temp);
        var objectDatas = _unclassified.ToComponentDataArray<ObjectDataCD>(Allocator.Temp);
        var em = EntityManager;

        for (int i = 0; i < entities.Length; i++)
        {
            // Mark unconditionally, including the ones that turn out not to be pylons — that is
            // what keeps this from being a per-frame sweep.
            em.AddComponent<NoBreakZonePylonScannedCD>(entities[i]);

            if (objectDatas[i].objectID == _pylonObjectID)
            {
                em.AddComponent<NoBreakZonePylonCD>(entities[i]);
            }
        }

        entities.Dispose();
        objectDatas.Dispose();
    }

    private void CollectPositions()
    {
        // 3단계 registers every pylon regardless of its variation. 기획서 §5 says a freshly placed
        // pylon starts switched off, but nothing can switch one on until 4단계 wires up the E key,
        // so filtering on variation here would mean no square ever exists and 체크포인트 1 could
        // not be tested. 4단계 adds `ObjectDataCD.variation == 1` to this and to the prefab's
        // variationIsDynamic flag together.
        var transforms = _pylons.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        // Compared position by position, which assumes the query returns pylons in a stable order.
        // It does in practice: chunk order only shifts when a pylon's own component set changes,
        // and that happens once, when it is first tagged. If the assumption ever breaks the cost is
        // a redundant re-evaluation, not a wrong answer — re-judging can only add protection.
        bool changed = transforms.Length != _positions.Length;
        for (int i = 0; i < transforms.Length; i++)
        {
            // The game resolves an entity to a tile this way too — float3 -> int2(round(x),
            // round(z)), the XZ plane (Pug.UnityExtensions/ExtensionMethods.cs:551).
            int2 tile = transforms[i].Position.RoundToInt2();

            if (i < _positions.Length)
            {
                if (!_positions[i].Equals(tile))
                {
                    changed = true;
                    _positions[i] = tile;
                }
            }
            else
            {
                _positions.Add(tile);
            }
        }

        if (transforms.Length < _positions.Length)
        {
            _positions.Resize(transforms.Length, NativeArrayOptions.UninitializedMemory);
        }

        transforms.Dispose();

        if (changed)
        {
            InvalidateEvaluatedObjects();
        }
    }

    // Placing a pylon has to protect the chests that were already standing there, and those have
    // long since been judged and tagged. Dropping the tag puts the whole world back through
    // NoBreakZoneProtectionSystem's discriminator on the next frame.
    //
    // This is a heavy structural change, which is exactly why it is tied to the only event that can
    // change an answer rather than run on a timer.
    //
    // 3단계 LIMITATION: re-judging can only ever add protection. Removing a pylon leaves everything
    // it was covering protected, because taking protection back off is 4단계 (that is what
    // NoBreakZoneProtectedCD was introduced for).
    private void InvalidateEvaluatedObjects()
    {
        if (_evaluated.IsEmpty)
        {
            return;
        }

        EntityManager.RemoveComponent<NoBreakZoneEvaluatedCD>(_evaluated);
        Debug.Log($"[NoBreakZone] pylons changed ({_positions.Length}) — re-evaluating (world={World.Name})");
    }
}
