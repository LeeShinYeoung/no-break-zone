using PugMod;
using Pug.UnityExtensions;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;  // GhostSimulationSystemGroup / PredictedSimulationSystemGroup — see the ordering note
using Unity.Transforms;
using UnityEngine;

// STAGE 3 — where the protected squares actually come from (design.md §13, stage 3).
//
// Stage 2 shipped with the pylon position hardcoded to the world origin, because the pylon object did
// not exist yet. It does now (Prefabs/NoBreakZonePylon.prefab), so this system finds the real ones
// and publishes their tile coordinates for NoBreakZoneProtectionSystem to read.
//
// HOW A PYLON IS RECOGNISED: by ObjectDataCD.objectID, compared against the id the game hands out
// for the name baked into ObjectAuthoring. API.Authoring.GetObjectID is a plain dictionary lookup
// over registered mod objects (ck-db Pug.Other/PugMod/ModAPIAuthoring.cs:28) and returns
// ObjectID.None for a name it does not know, which is also what it returns before the object
// database has finished loading — so nothing is classified until the lookup succeeds.
//
// WHY A TAG INSTEAD OF A PER-FRAME SWEEP (design.md §9, same reasoning as the protection system):
// deciding "is this a pylon" is a one-time answer per entity, so entities leave the discovery query
// permanently once asked. The recurring per-frame work is then proportional to the number of
// pylons, not to the number of objects in the world.
//
// THE ORDERING MIRRORS NoBreakZoneProtectionSystem'S, DELIBERATELY — read the long comment there for
// why those exact three constraints. This system has to move with it, not stay behind:
//   - ConsumeReleaseRequest is a one-frame latch with exactly one reader. Publisher and reader
//     ticking in different parts of the frame lets a release be missed or consumed a frame late.
//   - [UpdateBefore] is dropped when the two systems are in different groups, so leaving this one
//     in the ordinary bucket would silently hand the protection system last frame's Positions.
//   - Positions is a view into a NativeList that CollectPositions rewrites; readers inside the
//     simulation have to see it after this system has run and before the next rewrite.
// UpdateAfter(GhostSimulationSystemGroup) also matters here specifically: on the client a pylon's
// on/off state arrives as a ghost snapshot, and reading ObjectDataCD.variation before that lands
// would flicker protection off for a frame.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
[UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
[UpdateAfter(typeof(GhostSimulationSystemGroup))]
[UpdateBefore(typeof(PredictedSimulationSystemGroup))]
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
            // design.md §5: only a switched-on pylon exists as far as protection is concerned. A
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

    // design.md §6: "While switched on, the pylon is invulnerable … to recover it, switch it off first."
    //
    // This is deliberately NOT routed through NoBreakZoneProtectionSystem's discriminator, which
    // still excludes pylons. A pylon's own invulnerability has to follow its switch, not whether it
    // happens to stand in somebody's square — otherwise two pylons covering each other would leave
    // both permanently unrecoverable.
    //
    // Safe to own outright rather than checking for native indestructibility first, the way the
    // protection system must: this is our object and it ships without either component.
    //
    // IT TAKES BOTH COMPONENTS, because they guard different halves of that sentence and for a long
    // time only one of them was here. IndestructibleCD alone delivers "by pickaxes" and nothing else:
    // the player's own mining consults it, and everything that reaches an object some other way —
    // an explosion, a mob, environmental damage — goes through the shared HealthChangeBuffer, which
    // reads DontDestroyOnZeroHealthCD instead (the same split is spelled out in
    // NoBreakZoneProtectionSystem.Protect). So a switched-on pylon used to shrug off a pickaxe and
    // die to the first bomb thrown at it.
    private static void ApplySelfProtection(EntityManager em, Entity pylon, bool on)
    {
        ApplyIndestructible(em, pylon, on);
        ApplyDestroyGate(em, pylon, on);
    }

    /// What the player's own mining and attacks consult (PlayerController.DealDamageToObject reads
    /// this and nothing else).
    private static void ApplyIndestructible(EntityManager em, Entity pylon, bool on)
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

    /// The single gate every damage source passes through on its way to destroying something, and
    /// therefore the half that answers "by explosions … or by mob attacks".
    ///
    /// NOT TIED TO THE blockMobDamage SETTING, unlike an ordinary protected object. The protection
    /// system already carves out the same exception for tiles, and its comment says why: the
    /// setting decides what may finish off an installation, not whether the base still stands.
    /// design.md:322 puts the pylon on the second side of that line in as many words — "if the
    /// pylon breaks first, the whole base is left defenceless that very moment". A setting that can
    /// drop every square in the world by letting one mob through is not the choice that row is
    /// offering.
    ///
    /// Flag rather than component removal, for the reason Release() gives: NetCode fixes a ghost's
    /// component set at bake time, and the flag is the part the damage path actually reads.
    /// Switching the pylon off clears it in the same frame, so recovering one still only takes
    /// turning it off first — design.md §6's other half.
    private static void ApplyDestroyGate(EntityManager em, Entity pylon, bool on)
    {
        if (!em.HasComponent<DontDestroyOnZeroHealthCD>(pylon))
        {
            if (!on)
            {
                return;  // nothing to add and nothing to clear
            }

            em.AddComponentData(pylon, new DontDestroyOnZeroHealthCD { disabled = false });
            return;
        }

        // `disabled == on` is precisely the wrong state: guarding while off, or open while on.
        if (em.GetComponentData<DontDestroyOnZeroHealthCD>(pylon).disabled == on)
        {
            em.SetComponentData(pylon, new DontDestroyOnZeroHealthCD { disabled = !on });
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
