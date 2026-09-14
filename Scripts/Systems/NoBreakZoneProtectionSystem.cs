using System.Collections.Generic;
using Pug.UnityExtensions;
using PugTilemap;  // TileType — the tile enum lives here, not beside TileCD
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;  // GhostSimulationSystemGroup / PredictedSimulationSystemGroup — see the ordering note
using Unity.Transforms;
using UnityEngine;

// STAGE 2/3 — protection scoped to a square around a pylon (design.md §13, stages 2 and 3).
//
// Stage 1 shipped an unconditional version of this: every placeable in the world became
// indestructible. That was the point — it proved the mechanism. What it could not do is let anyone
// ever remodel their base. Stage 2 narrowed it to "inside a pylon's square" against a hardcoded
// coordinate; stage 3 replaced that constant with the real placed pylons, which
// NoBreakZonePylonRegistrySystem publishes just before this system runs.
//
// HOW PROTECTION WORKS (carried over from stage 1, verified in game — research.md chapter 9):
// Mining/attack/explosion damage runs PREDICTED on BOTH the client and the server world, and that
// path (PlayerController.DealDamageToObject) consults ONLY IndestructibleCD. A component added at
// runtime on the server is never replicated to clients, so a server-only fix left the client
// predicting the object's death every swing — invisible chest, phantom loot. The fix is to enable
// the game's own IndestructibleCD in BOTH worlds, so neither predicts any damage at all.
// DontDestroyOnZeroHealthCD is kept as a backstop for damage that bypasses DealDamageToObject.
//
// WHY A TAG INSTEAD OF A PER-FRAME SWEEP: design.md §9 forbids walking the world every frame. Entities
// leave the query permanently once judged (NoBreakZoneEvaluatedCD), so steady-state cost is
// proportional to newly streamed-in entities, not to base size.
//
// WHY THE ORDERING BELOW IS LOAD-BEARING, NOT TIDINESS.
// A TILE HAS NO ENTITY UNTIL IT IS HIT. TileDamageSystem creates one per damaged tile through
// BeginSimulationEntityCommandBufferSystem, so it materialises at the start of the NEXT frame with
// InitialHealthChange already enabled, and dies later in that same frame:
//
//   BeginSimulationEntityCommandBufferSystem   <- the tile damage entity appears here
//   GhostSimulationSystemGroup                 <- the client's variation snapshot lands here
//   *** this system ***                        <- and we tag it in time
//   PredictedSimulationSystemGroup (OrderFirst)
//       InitialHealthChangeSystem -> UpdateHealthFromBufferSystem -> SetEntitiesDestroyedSystem
//   ...ordinary SimulationSystemGroup systems  <- where this system used to be: too late
//
// A pickaxe hit is capped by DamageReductionCD.maxDamagePerHit, so the tile survives its first hit
// and its damage entity persists (it is only cleaned up once back at full health) — which is why
// floors resisted a pickaxe from the ordinary group and this looked like it worked. An explosion
// sets bypassMaxDamagePerHit, takes the tile out in one application, and never gave us a frame.
//
// THE THREE CONSTRAINTS ARE SPELLED OUT RATHER THAN INHERITED FROM A GROUP. The obvious move is
// [UpdateInGroup(typeof(BeforePredictedSimulationSystemGroup))] — that is where the game's own
// ImmunityZoneSystem sits, and it lands in the right place on the diagram above. It is not safe:
// that group declares UpdateAfter(GhostSimulationSystemGroup) and UpdateBefore(Predicted…), while
// BeginSimulationEntityCommandBufferSystem declares nothing but OrderFirst. There is no constraint
// path between the two, so which of them runs first is decided by ComponentSystemSorter's
// tie-break on the system type hash — stable, arbitrary, and not ours. Half the time the tile
// damage entity would not exist yet and this fix would do nothing at all.
//
// OrderFirst puts us in the same sorting bucket as those three (constraints across buckets are
// dropped), and then the edges are direct and cannot be reordered by anything else in the graph.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
[UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
[UpdateAfter(typeof(GhostSimulationSystemGroup))]
[UpdateBefore(typeof(PredictedSimulationSystemGroup))]
public partial class NoBreakZoneProtectionSystem : SystemBase
{
    private EntityQuery _candidates;
    private EntityQuery _ours;
    private EntityQuery _changedTiles;
    private NoBreakZonePylonRegistrySystem _registry;
    private readonly HashSet<ObjectID> _logged = new HashSet<ObjectID>();

    // The registry hands out int2s; NoBreakZoneRange takes parallel int arrays so it can stay free
    // of Unity types and be unit tested. Copied into reusable buffers once per frame rather than
    // per object.
    private int[] _pylonX = new int[8];
    private int[] _pylonZ = new int[8];
    private int _pylonCount;

    private bool _warnedMissingObjectInfo;

    // Last diameter acted on. A settings change is the only other thing that can alter an answer,
    // so it re-judges the world the same way a pylon appearing does; without this a player would
    // change the setting and see nothing until they switched a pylon off and on.
    private int _appliedDiameter = -1;

    protected override void OnCreate()
    {
        // Both systems are managed and run in order on the main thread, so reading the registry's
        // published positions during OnUpdate needs no copy or job dependency.
        _registry = World.GetOrCreateSystemManaged<NoBreakZonePylonRegistrySystem>();

        // LocalTransform is required now: it is how the game itself resolves an entity to a tile
        // (Position.RoundToInt2(), see DetectRoomSystem). Anything without one has no place on the
        // grid and cannot be inside a square.
        // NO ObjectTypeCD HERE, THOUGH THE RULE IS ABOUT OBJECT TYPE. A mod's objects never have it.
        // Vanilla objects are authored with EntityMonoBehaviourData and EntityMonoBehaviourDataConverter
        // gives them ObjectTypeCD; a mod authors with ObjectAuthoring, and ObjectConverter — the only
        // other path — adds IsObjectCD, ObjectDataCD and ObjectCategoryTagsCD but no ObjectTypeCD.
        // Those two converters are the only places in the game that add it (verified by decompiling
        // Pug.ECS.Conversion.dll; research.md chapter 20).
        //
        // So requiring it here quietly excluded every object this mod adds — which is why the mod's
        // own workbench broke inside its own protected square, with no PROTECT and no skip line to
        // show for it. The type now comes from the object database, which answers for vanilla and
        // modded objects alike.
        _candidates = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<HealthCD>(),
                ComponentType.ReadOnly<ObjectDataCD>(),
                ComponentType.ReadOnly<LocalTransform>(),
            },
            None = new[]
            {
                // TileCD used to be excluded here. Walls and floors are part of a base too
                // (design.md §4), and the rule now judges them rather than the query dropping them —
                // see NoBreakZoneProtectionRule's tile branch for how resource duplication stays
                // impossible.
                ComponentType.ReadOnly<NoBreakZoneEvaluatedCD>(),

                // A pylon stands inside its own square, so without this it would protect itself.
                // design.md §6 does want that eventually ("while switched on, the pylon is
                // invulnerable"), but the same sentence continues "to recover it, switch it off
                // first" — and nothing can switch a pylon off until stage 4. Self-protecting it now
                // would mean a pylon placed during checkpoint 1
                // could never be picked up again. NoBreakZonePylonRegistrySystem.ApplySelfProtection
                // grants the pylon both guards directly, tied to its variation, rather than through
                // this discriminator.
                ComponentType.ReadOnly<NoBreakZonePylonCD>(),
            },
        });

        // Everything this mod made indestructible, and nothing else. Switching a pylon off has to
        // give these back — see ReleaseUncovered.
        _ours = GetEntityQuery(
            ComponentType.ReadOnly<NoBreakZoneProtectedCD>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<ObjectDataCD>());

        // A TILE CAN CHANGE INTO A DIFFERENT TILE, AND THE ANSWER HAS TO CHANGE WITH IT.
        //
        // NoBreakZoneEvaluatedCD exists so an object is judged once (design.md §9), and for an object
        // that is a sound assumption: a chest never becomes an ore boulder. A tilemap square is not
        // like that. Judge one as WALL — protected since design.md's 2026-08-07 decision — let it
        // become ORE, and the stale answer keeps the ore indestructible. A protected ore tile never
        // depletes, which is the resource duplication design.md §6 forbids above everything else.
        //
        // The change filter is what keeps this from undoing the tag's whole purpose: it matches only
        // chunks whose TileCD was actually written since this system last ran, so a base full of
        // untouched walls costs nothing.
        _changedTiles = GetEntityQuery(new EntityQueryDesc
        {
            All = new[]
            {
                ComponentType.ReadOnly<TileCD>(),
                ComponentType.ReadOnly<NoBreakZoneEvaluatedCD>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<NoBreakZonePylonCD>(),
            },
        });
        _changedTiles.SetChangedVersionFilter(ComponentType.ReadOnly<TileCD>());
    }

    protected override void OnUpdate()
    {
        var pylons = _registry.Positions;
        int diameter = NoBreakZoneConfig.ProtectionDiameter;
        int radius = NoBreakZoneRange.RadiusFromDiameter(diameter);
        CachePylons(pylons);

        bool diameterChanged = _appliedDiameter != diameter;
        if (diameterChanged)
        {
            // Same treatment a pylon change gets: drop the evaluated tags so everything is judged
            // again against the new square.
            _appliedDiameter = diameter;
            _registry.RequestReevaluation();
        }

        // Before judging anything new: if the set of switched-on pylons just changed, hand back
        // whatever fell outside it. This has to run before the early exits below — switching the
        // last pylon off leaves no squares and no new candidates, and is exactly the case where
        // releasing matters most.
        if (_registry.ConsumeReleaseRequest())
        {
            ReleaseUncovered(radius);
        }

        // Then: any tile that has become a different tile since we judged it goes back in the queue.
        // Ordered after the release above and before the early exits below for the same reason that
        // one is — a tile that turned into ore has to be handed back even in a frame where nothing
        // new is waiting to be judged.
        ReJudgeChangedTiles();

        if (_candidates.IsEmpty)
        {
            return;
        }

        if (pylons.Length == 0)
        {
            // No switched-on pylon means no square, so nothing can qualify (design.md §6). Leaving
            // early also leaves the candidates untagged, so they get judged properly once one is
            // switched on rather than being written off now.
            return;
        }

        // Copy first: adding components below is a structural change that would invalidate live
        // chunk iteration.
        var entities = _candidates.ToEntityArray(Allocator.Temp);
        var objectDatas = _candidates.ToComponentDataArray<ObjectDataCD>(Allocator.Temp);
        var transforms = _candidates.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var em = EntityManager;

        for (int i = 0; i < entities.Length; i++)
        {
            var entity = entities[i];

            // Mark first, and unconditionally: an entity we decided not to protect must not come
            // back through the query next frame.
            em.AddComponent<NoBreakZoneEvaluatedCD>(entity);

            bool destructible = em.HasComponent<DestructibleObjectCD>(entity);
            bool lootTable = em.HasComponent<DropsLootFromLootTableCD>(entity);
            bool lootOnDamage = em.HasComponent<DropsLootWhenDamagedCD>(entity);
            bool isTile = em.HasComponent<TileCD>(entity);
            int objectType = ObjectTypeOf(objectDatas[i]);

            bool qualifies = NoBreakZoneProtectionRule.ShouldProtect(
                objectType,
                true, // HealthCD is in the query's All list
                isTile,
                destructible,
                lootTable,
                lootOnDamage,
                isOreTile: isTile && em.GetComponentData<TileCD>(entity).tileType == TileType.ore,
                requiresDrill: em.HasComponent<RequiresDrillCD>(entity),
                isPlant: em.HasComponent<PlantCD>(entity) || em.HasComponent<GrowingCD>(entity));

            if (!qualifies)
            {
                continue;
            }

            // design.md §6: every tile the object stands on has to be covered, not just the one its
            // transform sits at.
            if (!IsFootprintCovered(em, entity, transforms[i], objectDatas[i], radius))
            {
                continue;
            }

            Protect(em, entity, objectDatas[i].objectID, isTile);
        }

        entities.Dispose();
        objectDatas.Dispose();
        transforms.Dispose();
    }

    // STAGE 4 — the other half of the switch (design.md §6: "to change the base, switch the pylon off").
    //
    // Up to stage 3 this system only ever added protection, which made the toggle pointless: turning a
    // pylon off left every chest around it just as indestructible as before. This gives them back.
    //
    // ONLY ENTITIES CARRYING NoBreakZoneProtectedCD ARE TOUCHED, and that tag is only ever applied
    // to something that was destructible when we found it (see Protect). Objects the game ships
    // indestructible — the Core, boss-arena scenery — never receive it, so no amount of pylon
    // switching can make them breakable. That is the whole reason the tag exists.
    //
    // An object still covered by some other switched-on pylon keeps everything, so overlapping
    // squares behave the way design.md §6 describes when only one of them is switched off.
    private void ReleaseUncovered(int radius)
    {
        if (_ours.IsEmpty)
        {
            return;
        }

        var entities = _ours.ToEntityArray(Allocator.Temp);
        var transforms = _ours.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var objectDatas = _ours.ToComponentDataArray<ObjectDataCD>(Allocator.Temp);
        var em = EntityManager;
        int released = 0;

        for (int i = 0; i < entities.Length; i++)
        {
            // Deliberately the same call the protect path makes. If the two ever disagreed about
            // what an object occupies, something could qualify for protection and never qualify for
            // release — indestructible forever, with the pylon switched off.
            if (IsFootprintCovered(em, entities[i], transforms[i], objectDatas[i], radius))
            {
                continue;
            }

            Release(em, entities[i]);
            released++;
        }

        entities.Dispose();
        transforms.Dispose();
        objectDatas.Dispose();

        if (released > 0)
        {
            Debug.Log($"[NoBreakZone] released {released} object(s) (world={World.Name})");
        }
    }

    /// Drops our answer for every tile whose TileCD was rewritten since the last pass, so the
    /// discriminator sees it again with its new identity.
    ///
    /// Releasing first matters. A tile judged as a wall is holding DontDestroyOnZeroHealthCD; if it
    /// is now ore, simply re-queuing it is not enough, because the discriminator only ever ADDS
    /// protection — it has no path that takes it away from something it decides against. Release
    /// hands back exactly what we gave and nothing the game owned itself.
    private void ReJudgeChangedTiles()
    {
        if (_changedTiles.IsEmpty)
        {
            return;
        }

        var em = EntityManager;
        using var entities = _changedTiles.ToEntityArray(Allocator.Temp);

        for (int i = 0; i < entities.Length; i++)
        {
            Entity entity = entities[i];

            if (em.HasComponent<NoBreakZoneProtectedCD>(entity))
            {
                Release(em, entity);
            }

            em.RemoveComponent<NoBreakZoneEvaluatedCD>(entity);
        }

        Debug.Log($"[NoBreakZone] re-judging {entities.Length} tile(s) that changed type "
                  + $"(world={World.Name})");
    }

    private static void Release(EntityManager em, Entity entity)
    {
        // Disable rather than remove: NetCode fixes a ghost's component set at bake time, and the
        // enable flag is the part the damage path actually reads (research.md chapter 9).
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

    private void CachePylons(NativeArray<int2> pylons)
    {
        if (_pylonX.Length < pylons.Length)
        {
            _pylonX = new int[pylons.Length];
            _pylonZ = new int[pylons.Length];
        }

        for (int i = 0; i < pylons.Length; i++)
        {
            _pylonX[i] = pylons[i].x;
            _pylonZ[i] = pylons[i].y;
        }

        _pylonCount = pylons.Length;
    }

    // design.md §6, both rules at once: every tile the object occupies must be covered, and each of
    // them may be covered by a different pylon.
    //
    // The size comes from the object database rather than from anything on the entity, and the
    // walk mirrors the game's own in DetectRoomSystem — corner offset first, then the tile span,
    // with DirectionCD rotating both for objects that can be turned.
    private bool IsFootprintCovered(
        EntityManager em, Entity entity, LocalTransform transform, ObjectDataCD data, int radius)
    {
        int2 origin = transform.Position.RoundToInt2();
        int2 size = new int2(1, 1);
        int2 corner = int2.zero;

        var info = PugDatabase.GetObjectInfo(data.objectID, data.variation);
        if (info != null)
        {
            size = new int2(info.prefabTileSize.x, info.prefabTileSize.y);
            corner = new int2(info.prefabCornerOffset.x, info.prefabCornerOffset.y);
        }
        else if (!_warnedMissingObjectInfo)
        {
            // Falling back to one tile is the safe direction — the object stays protectable — but
            // it also silently restores the old origin-only behaviour, so say so once rather than
            // letting it look like the multi-tile rule is working.
            _warnedMissingObjectInfo = true;
            Debug.LogWarning($"[NoBreakZone] no ObjectInfo for {data.objectID}; treating objects "
                             + "without database entries as 1x1 (world=" + World.Name + ")");
        }

        if (em.HasComponent<DirectionCD>(entity))
        {
            em.GetComponentData<DirectionCD>(entity)
                .GetPrefabOffsetAndTileSize(corner, size, out corner, out size);
        }

        NoBreakZoneFootprint.Rect(
            origin.x, origin.y, size.x, size.y, corner.x, corner.y,
            out int minX, out int minZ, out int maxX, out int maxZ);

        return NoBreakZoneRange.AllTilesCovered(
            _pylonX, _pylonZ, _pylonCount, minX, minZ, maxX, maxZ, radius);
    }

    /// The object's type, from the database rather than from a component on the entity.
    ///
    /// ObjectTypeCD would be the obvious source and is the wrong one: only the vanilla authoring path
    /// produces it, so reading it there classified every modded object as "not a placeable" — see the
    /// note on the candidate query. The database is populated from ObjectInfo for vanilla and modded
    /// objects alike, which is how the [NBZDB] audit could report our workbench as PlaceablePrefab
    /// while the system saw nothing at all.
    ///
    /// A missing entry means "not something we know how to judge", so it falls back to a type the
    /// rule refuses. That is the safe direction: an unknown object stays breakable.
    private static int ObjectTypeOf(ObjectDataCD data)
    {
        var info = PugDatabase.GetObjectInfo(data.objectID, data.variation);
        return info == null ? 0 : (int)info.objectType;
    }

    private void Protect(EntityManager em, Entity entity, ObjectID objectID, bool isTile)
    {
        // A TILE IS NOT REACHED THROUGH IndestructibleCD. Mining a wall or a floor goes through
        // TileDamageSystem, and that system does not read IndestructibleCD at all — it writes a
        // HealthChange into the shared buffer and lets SetEntitiesDestroyedSystem decide. So for a
        // tile the DontDestroyOnZeroHealthCD below is the whole mechanism, and adding the other
        // component would be dead weight on a great many entities.
        //
        // TileDamageSystem runs in PredictedSimulationSystemGroup, so the client predicts the break
        // too — the same trap that made chests into ghosts in research.md chapter 9. This system
        // already runs in both worlds, so both refuse alike.
        if (!isTile)
        {
            // Leave objects that were already indestructible alone, and do not claim them as ours —
            // otherwise stage 4 would "restore" them to destructible when a pylon switches off.
            bool alreadyNativelyIndestructible =
                em.HasComponent<IndestructibleCD>(entity)
                && em.IsComponentEnabled<IndestructibleCD>(entity);

            if (!alreadyNativelyIndestructible)
            {
                em.AddComponent<IndestructibleCD>(entity);
                em.SetComponentEnabled<IndestructibleCD>(entity, true);
                em.AddComponent<NoBreakZoneProtectedCD>(entity);
            }
        }
        else if (!em.HasComponent<DontDestroyOnZeroHealthCD>(entity)
                 || em.GetComponentData<DontDestroyOnZeroHealthCD>(entity).disabled)
        {
            // Same discipline as above, read through the component that actually guards a tile:
            // claim it only if it was breakable when we found it, so switching a pylon off can
            // never turn a natively indestructible tile into rubble.
            em.AddComponent<NoBreakZoneProtectedCD>(entity);
        }

        // design.md §10's "block mob damage". The two components guard different paths:
        // IndestructibleCD above is what the player's own mining and attacks consult, while this one
        // guards the single gate every damage source passes through (research.md chapters 8 and 9).
        // Leaving it off is therefore exactly "off blocks only player-caused damage" — mobs and
        // explosions can still finish something off.
        //
        // A tile has no other guard, so its protection is not optional in the same way: the setting
        // decides what may finish off an installation, not whether a wall stands.
        bool blockEverything = NoBreakZoneConfig.BlockMobDamage || isTile;
        if (!em.HasComponent<DontDestroyOnZeroHealthCD>(entity))
        {
            if (blockEverything)
            {
                em.AddComponentData(entity, new DontDestroyOnZeroHealthCD { disabled = false });
            }
        }
        else
        {
            // Already present, from us or from the game. Only the flag is ours to move.
            em.SetComponentData(entity, new DontDestroyOnZeroHealthCD { disabled = !blockEverything });
        }

        if (_logged.Add(objectID))
        {
            Debug.Log($"[NoBreakZone] PROTECT {objectID} (world={World.Name})");
        }
    }
}
