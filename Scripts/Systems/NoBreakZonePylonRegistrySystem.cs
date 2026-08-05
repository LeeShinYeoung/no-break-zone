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
    private bool _protectionNeedsRelease;

    /// Tile coordinates of every switched-on pylon. Valid for the rest of the frame once this
    /// system has run; NoBreakZoneProtectionSystem is ordered after it and reads this directly.
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
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<ObjectDataCD>());

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
        var entities = _pylons.ToEntityArray(Allocator.Temp);
        var transforms = _pylons.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var objectDatas = _pylons.ToComponentDataArray<ObjectDataCD>(Allocator.Temp);
        var em = EntityManager;

        // Compared position by position, which assumes the query returns pylons in a stable order.
        // It does in practice: chunk order only shifts when a pylon's own component set changes,
        // and that settles after the first frame it is seen. If the assumption ever breaks the cost
        // is a redundant re-evaluation, not a wrong answer.
        bool changed = false;
        int active = 0;

        for (int i = 0; i < entities.Length; i++)
        {
            // 기획서 §5: only a switched-on pylon exists as far as protection is concerned. A
            // switched-off one keeps its tag and its entity — it simply projects no square.
            bool on = objectDatas[i].variation == NoBreakZonePylonGraphics.VariationOn;

            ApplySelfProtection(em, entities[i], on);

            if (!on)
            {
                continue;
            }

            // The game resolves an entity to a tile this way too — float3 -> int2(round(x),
            // round(z)), the XZ plane (Pug.UnityExtensions/ExtensionMethods.cs:551).
            int2 tile = transforms[i].Position.RoundToInt2();

            if (active < _positions.Length)
            {
                if (!_positions[active].Equals(tile))
                {
                    changed = true;
                    _positions[active] = tile;
                }
            }
            else
            {
                changed = true;
                _positions.Add(tile);
            }

            active++;
        }

        if (active < _positions.Length)
        {
            changed = true;
            _positions.Resize(active, NativeArrayOptions.UninitializedMemory);
        }

        entities.Dispose();
        transforms.Dispose();
        objectDatas.Dispose();

        if (changed)
        {
            InvalidateEvaluatedObjects();
        }
    }

    // 기획서 §6: "켜져 있는 동안 파일런은 무적이다 … 회수하려면 먼저 꺼야 한다."
    //
    // This is deliberately NOT routed through NoBreakZoneProtectionSystem's discriminator, which
    // still excludes pylons. A pylon's own invulnerability has to follow its switch, not whether it
    // happens to stand in somebody's square — otherwise two pylons covering each other would leave
    // both permanently unrecoverable.
    //
    // Safe to own outright rather than checking for native indestructibility first, the way the
    // protection system must: this is our object and it ships without IndestructibleCD.
    private static void ApplySelfProtection(EntityManager em, Entity pylon, bool on)
    {
        if (!em.HasComponent<IndestructibleCD>(pylon))
        {
            if (!on)
            {
                return;  // nothing to add and nothing to clear
            }

            em.AddComponent<IndestructibleCD>(pylon);
        }

        if (em.IsComponentEnabled<IndestructibleCD>(pylon) != on)
        {
            em.SetComponentEnabled<IndestructibleCD>(pylon, on);
        }
    }

    // Placing a pylon has to protect the chests that were already standing there, and those have
    // long since been judged and tagged. Dropping the tag puts the whole world back through
    // NoBreakZoneProtectionSystem's discriminator on the next frame.
    //
    // This is a heavy structural change, which is exactly why it is tied to the only event that can
    // change an answer rather than run on a timer.
    //
    // Switching one off has to work the same way in reverse, which is what NoBreakZoneProtectedCD
    // is for: NoBreakZoneProtectionSystem sweeps the objects it claimed and releases the ones no
    // longer covered. Both directions hang off this one event.
    private void InvalidateEvaluatedObjects()
    {
        if (!_evaluated.IsEmpty)
        {
            EntityManager.RemoveComponent<NoBreakZoneEvaluatedCD>(_evaluated);
        }

        // Tell the protection system to re-check what it already owns, even when no candidate is
        // waiting — switching the last pylon off has to release everything, and that path adds no
        // new candidates at all.
        _protectionNeedsRelease = true;

        Debug.Log($"[NoBreakZone] active pylons: {_positions.Length} — re-evaluating (world={World.Name})");
    }

    /// Force the same re-judgement a pylon change causes. Used when something other than the pylons
    /// alters the answer — currently only the protection diameter setting.
    public void RequestReevaluation()
    {
        InvalidateEvaluatedObjects();
    }

    /// True for one frame after the set of switched-on pylons changed. Read and cleared by
    /// NoBreakZoneProtectionSystem, which runs immediately after this system.
    public bool ConsumeReleaseRequest()
    {
        bool requested = _protectionNeedsRelease;
        _protectionNeedsRelease = false;
        return requested;
    }
}
