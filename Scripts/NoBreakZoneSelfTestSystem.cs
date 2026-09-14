using System.Collections.Generic;
using Pug.UnityExtensions;
using PugMod;
using PugTilemap;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// The only check that runs inside the real game, and therefore the only one that can answer the
// question the whole tile fix turns on: does the mod actually win the frame-order race against the
// game's damage pipeline?
//
// WHY IT EXISTS. Everything else in the verification ladder (Editor/Docs/workflow.md) proves the mod
// behaves correctly given a world state we constructed ourselves. None of it can prove the game
// hands it that state at the right moment, because the game's damage systems are Burst jobs that
// need a database, a tilemap and a NetCode world. Answering that used to cost a play session per
// attempt — three of them went on one pylon sprite. This turns it into a line in Player.log.
//
// WHAT ANYONE HAS TO DO: nothing but be connected, when it runs on the dedicated server
// (Editor/Server/start-server.ps1) — it builds its own switched-on pylon and reports. In a real
// world a human can instead place a pylon within the first ten seconds and it will use that one.
// Either way the verdict is greppable out of the log:
//
//     [NBZTEST] floor-inside-explosion PASS
//     [NBZTEST] SUMMARY pass=6 fail=0 skip=0
//
// OFF BY DEFAULT, AND IT HAS TO STAY THAT WAY. It damages tiles on purpose — including outside the
// square, where they are supposed to break — so it must never run in somebody's real base.
//
// ONE RUN PER WORLD. Being destructive is also why it does not repeat: a second run would be
// standing on the wreckage of the first, and on 2026-09-08 that produced failures that said nothing
// about the mod. Restart the server for another verdict — it discards the world on the way up.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class NoBreakZoneSelfTestSystem : PugSimulationSystemBase
{
    // Explosion-shaped tile damage. These are the flags ExplosionDamageSystem itself sets
    // (ExplosionDamageSystem: canHitLowColliders, bypassMaxDamagePerHit, damagedByExplosion), and
    // bypassMaxDamagePerHit is the one that made this bug: it removes the per-hit cap that lets a
    // tile survive its first pickaxe swing, so the tile dies in the same frame its damage entity is
    // created. If the mod is late by one frame, this is what finds out.
    private const int ExplosionDamage = 9999;

    // Pickaxe-shaped: capped, several hits to kill. This is the path that already worked, kept so a
    // future change cannot quietly trade one for the other.
    private const int PickaxeDamage = 20;

    private const int FramesBetweenSteps = 12;

    /// One past the last case. Finish runs here, so adding a case means adding to the switch and
    /// moving this.
    private const int FinalStep = 34;

    /// Where each group of cases that can stand on its own begins.
    ///
    /// A step that finds its prerequisite missing skips FORWARD TO THE NEXT GROUP, not to the end.
    /// Jumping to the end used to take unrelated cases with it — a run that could not switch the
    /// pylon off lost all three placeable cases with no PASS, no FAIL and no SKIP to say so. The
    /// whole point of this suite is that one connection answers as many questions as it can, and a
    /// missing wall is no reason to stop asking about workbenches.
    private const int PlaceableStep = 17;
    private const int PylonInvulnStep = 20;
    private const int BoulderStep = 23;
    private const int ScenarioStep = 26;

    // Generous on purpose. On a dedicated server the map only streams in once somebody connects, so
    // this has to outlast a human launching the game, picking a character and joining — not just the
    // few seconds it takes to walk to a pylon in a world that is already open.
    private const int SetupTimeoutFrames = 36000;

    // How far from the world origin to look for somewhere to build. The Core stands at the origin,
    // so this is the part of the map guaranteed to be generated — and, as the first run of this
    // proved by having its pylon deleted out from under it, guaranteed to contain walls too.
    private const int SearchRadius = 40;

    // How long to wait for a human-placed pylon before building one. On a server there is never
    // going to be one; in a real world this is long enough to walk over and switch one on.
    private const int FramesBeforeSelfProvisioning = 600;

    // Radius, in tiles, of the area the test pins in memory. Has to cover the pylon's square, the
    // outside probe beyond it, and the search that finds them — with room to spare.
    private const float KeepLoadedRadius = 90f;

    private NoBreakZonePylonRegistrySystem _registry;
    private Entity _areaAnchor = Entity.Null;
    private bool _announcedNoGround;
    private bool _announcedNoProbes;
    private bool _floorIsDamageable;

    // Rising-edge detector on the config flag. Flipping selfTest off and on again re-runs the whole
    // suite without restarting anything — which is the difference between "the human rejoins for
    // every check" and "the human joins once and I re-run as often as I like".
    private bool _armed;
    private int _run;

    // Each retry builds somewhere new, so a spot the game keeps refusing — the Core's own tile, for
    // one — does not trap the run in a loop.
    private Entity _lastSpawned = Entity.Null;
    private int _spawnAttempt;

    // Roughly a minute at 60 ticks. Only SETUP FAIL waits this out and tries again — a run that
    // actually reported is the last one this world gets, for the reason spelled out in OnUpdate.
    private const int FramesBetweenRuns = 3600;
    private int _framesUntilRerun;

    private int _step;
    private int _wait;
    private int _framesWaitingForPylon;
    private int _pass;
    private int _fail;
    private int _skip;

    private int2 _inside;
    private int2 _outside;
    private int2 _insideWall;
    private int2 _outsideWall;
    private bool _haveWalls;
    private int2 _insideOre;
    private int2 _outsideOre;
    private bool _haveOre;
    private int2 _pylon;
    private int _pickaxeSwings;
    private Entity _insidePlaceable = Entity.Null;
    private Entity _outsidePlaceable = Entity.Null;
    private Entity _protectedPylon = Entity.Null;
    private Entity _controlPylon = Entity.Null;
    private Entity _insideBoulder = Entity.Null;
    private Entity _outsideBoulder = Entity.Null;
    private Entity _scenarioChest = Entity.Null;
    private int2 _scenarioWall;
    private bool _haveScenarioWall;
    private int _scenarioSwings;
    private int _toggleCycles;

    /// Enough capped hits to fell a wall. The cap is per hit, so this is the only way to tell the
    /// pickaxe path apart from the explosion one.
    private const int PickaxeSwings = 20;
    private int _tileset;

    /// Ceiling on a bench whose recipes are all drawn at once: the crafting window shows three
    /// pages of six (research.md chapter 18). Past it a recipe is in the list and never on screen.
    private const int MaxDrawableRecipeSlots = 18;

    /// One of the ten ore boulders in object_flags.csv, all of which carry the same flags. Copper is
    /// the cheapest and exists in every world, so it is the one this asks about.
    private const string BoulderObjectName = "CopperOreBoulder";

    /// How long to wait for the recipe pass before giving up on it. A minute — the object database
    /// is up long before the map is, so this never being reached is the normal case.
    private const int RecipeTimeoutFrames = 3600;

    private bool _recipesJudged;
    private int _framesWaitingForRecipes;

    private static readonly string[] RecipeCaseNames =
    {
        "recipe-bench-at-automation-table",
        "recipe-pylon-at-our-bench",
        "recipe-lens-at-our-bench",
        "recipe-remote-at-our-bench",
        "recipe-bench-shows-three",
    };

    protected override void OnCreate()
    {
        NeedDatabase();
        NeedTileUpdateBuffer();
        NeedTileDamageBuffer();

        _registry = World.GetOrCreateSystemManaged<NoBreakZonePylonRegistrySystem>();

        base.OnCreate();
    }

    protected override void OnUpdate()
    {
        // Deliberately not `Enabled = false`: staying in the loop is what lets a later flip of the
        // flag start another run. The cost is one boolean read per frame on a server that is only
        // ever running this when somebody asked for it.
        if (!NoBreakZoneConfig.SelfTest)
        {
            _armed = false;
            base.OnUpdate();
            return;
        }

        if (!_armed)
        {
            _armed = true;
            StartRun();
        }

        JudgeRecipesIfReady();

        if (_step == FinalStep)
        {
            Finish();
        }

        if (_step > FinalStep)
        {
            // ONE COMPLETED RUN PER WORLD, AND NO MORE. This used to re-arm every minute, and the
            // repeat runs were worse than useless: the suite is destructive, nothing puts the
            // terrain back, and so run 2 works on what run 1 chewed up. Watching it happen on
            // 2026-09-08 is what settled this — release-on-switch-off passed on run 1 and failed on
            // runs 2 through 6, and the ore pair degraded to SKIP once the earlier runs had blown up
            // the only ore in reach. A red that means "the previous run ate the evidence" is worse
            // than no verdict at all, because somebody has to spend a session finding that out.
            //
            // To run again, restart the server: it now discards the world, which is the only way to
            // get the clean terrain a second run would need anyway. Flipping selfTest off and on
            // still re-arms too, for a world somebody is deliberately reusing.
            //
            // _framesUntilRerun still runs the countdown for SETUP FAIL below, which is a different
            // case: nothing was measured and nothing was destroyed, so retrying costs nothing and
            // buys the human the freedom to join whenever they like.
            if (_framesUntilRerun > 0 && --_framesUntilRerun <= 0)
            {
                _armed = false;
            }

            base.OnUpdate();
            return;
        }

        if (_wait > 0)
        {
            _wait--;
            base.OnUpdate();
            return;
        }

        switch (_step)
        {
            case 0: WaitForPylon(); break;
            case 1: LayTestFloors(); break;
            case 2: HitFloorsLikeAnExplosion(); break;
            case 3: CheckExplosionResult(); break;
            case 4: HitInsideLikeAPickaxe(); break;
            case 5: CheckPickaxeResult(); break;
            case 6: RequestDigs(); break;
            case 7: CheckDigResult(); break;
            case 8: RequestClearThenAdd(); break;
            case 9: CheckClearThenAddResult(); break;
            case 10: BlowUpOre(); break;
            case 11: CheckOreResult(); break;
            case 12: MineTheWalls(); break;
            case 13: CheckWallPickaxeResult(); break;
            case 14: SwitchPylonOff(); break;
            case 15: BlowUpTheReleasedWall(); break;
            case 16: CheckReleaseResult(); break;
            case 17: SpawnPlaceables(); break;
            case 18: DamagePlaceables(); break;
            case 19: CheckPlaceableResult(); break;
            case 20: SpawnControlPylon(); break;
            case 21: DamagePylons(); break;
            case 22: CheckPylonResult(); break;
            case 23: SpawnBoulders(); break;
            case 24: SettleBoulders(); break;
            case 25: CheckBoulderResult(); break;
            case 26: ScenarioSetUp(); break;
            case 27: ScenarioMineAndLeaveAlone(); break;
            case 28: ScenarioCheckIdle(); break;
            case 29: ScenarioSwitchOffAndWatch(); break;
            case 30: ScenarioCheckAfterSwitchOff(); break;
            case 31: ScenarioToggleCycle(); break;
            case 32: ScenarioCheckToggleThenMine(); break;
            case 33: ScenarioCheckRelease(); break;
            default: break;
        }

        base.OnUpdate();
    }

    // ----------------------------------------------------------------------------------- recipes

    /// design.md §4's door into the whole mod, judged once per run.
    ///
    /// WHY IT IS WORTH CHECKING AT ALL. Our bench's three recipes are authored by NAME
    /// (Editor/genassets.py's crafts=[...]) and the game's own bake resolves those names to ids.
    /// That resolution has already failed silently once: the converter beside
    /// NoBreakZoneRecipeInjectionSystem asked for a numeric id before the mod's objects existed,
    /// got None, and dropped the bench's own recipe without a word. When it fails the mod is simply
    /// unreachable — nothing can be crafted, so nothing else in this suite ever gets the chance to
    /// be wrong — and until now nothing short of a human opening a crafting window would notice.
    ///
    /// OUTSIDE THE STEP MACHINE, ON PURPOSE. It needs no pylon, no ground and no tilemap. Running
    /// it here means a SETUP FAIL — which is what a map that never streams in produces — still
    /// leaves five verdicts in the log instead of none.
    private void JudgeRecipesIfReady()
    {
        if (_recipesJudged)
        {
            return;
        }

        _framesWaitingForRecipes++;

        // Waiting on the injection pass rather than on a frame count. The two systems sit in
        // SimulationSystemGroup with no ordering between them, so reading the buffers on our own
        // schedule could judge them before anything had been written — a FAIL that says nothing
        // about the mod. Guessing at frame order is the mistake that hid the explosion bug.
        var injection = World.GetExistingSystemManaged<NoBreakZoneRecipeInjectionSystem>();

        if (!database.IsCreated || injection == null || !injection.Done)
        {
            if (_framesWaitingForRecipes < RecipeTimeoutFrames)
            {
                return;
            }

            SkipRecipeCases(injection == null
                ? "the recipe injection system is not in this world"
                : "the recipe injection pass never reported done");
            _recipesJudged = true;
            return;
        }

        JudgeRecipes();
        _recipesJudged = true;
    }

    private void JudgeRecipes()
    {
        ObjectID bench = API.Authoring.GetObjectID(NoBreakZoneObjectNames.Workbench);
        if (bench == ObjectID.None)
        {
            SkipRecipeCases($"the object database does not know {NoBreakZoneObjectNames.Workbench}");
            return;
        }

        // design.md §4 / coverage.md #38. The one recipe the mod cannot author itself, because only the
        // game owns this bench — and the target is read off the system that injects it, so the two
        // cannot drift apart.
        ObjectID host = NoBreakZoneRecipeInjectionSystem.TargetWorkbench;
        if (TryReadRecipes(host, out List<ObjectID> hostOffers, out _, out _))
        {
            Verdict("recipe-bench-at-automation-table",
                hostOffers.Contains(bench),
                $"{host} offers {NoBreakZoneObjectNames.Workbench}");
        }
        else
        {
            Skip("recipe-bench-at-automation-table", $"{host} has no craftable prefab in this world");
        }

        if (!TryReadRecipes(bench, out List<ObjectID> ourOffers, out int slots, out int categories))
        {
            SkipRecipeCases("our own workbench has no craftable prefab in this world", skipHost: false);
            return;
        }

        // coverage.md #1 · #31 · #33 — one verdict each, so a single name that failed to resolve is
        // named in the log rather than hidden inside a combined result.
        int found = 0;
        found += JudgeOneRecipe("recipe-pylon-at-our-bench", NoBreakZoneObjectNames.Pylon, ourOffers);
        found += JudgeOneRecipe("recipe-lens-at-our-bench", NoBreakZoneObjectNames.Lens, ourOffers);
        found += JudgeOneRecipe("recipe-remote-at-our-bench", NoBreakZoneObjectNames.Remote, ourOffers);

        // coverage.md #40, and a different question from the three above: being in the list is not
        // being on screen. research.md chapter 18 is the record of that difference costing play
        // sessions — the recipe was in the iron workbench's list the whole time and never drawn,
        // because the list ran past 18 slots and was split into ranges the UI pages through one at a
        // time.
        bool drawable = slots <= MaxDrawableRecipeSlots && categories == 0;
        Verdict("recipe-bench-shows-three",
            found == 3 && drawable,
            "all three sit in a list the crafting window draws in one go "
            + $"(found {found}/3, slots={slots} of {MaxDrawableRecipeSlots}, categories={categories})");
    }

    /// One recipe, named, so the log says which of the three names failed to resolve.
    private int JudgeOneRecipe(string caseName, string objectName, List<ObjectID> offers)
    {
        ObjectID id = API.Authoring.GetObjectID(objectName);
        if (id == ObjectID.None)
        {
            Skip(caseName, $"the object database does not know {objectName}");
            return 0;
        }

        bool listed = offers.Contains(id);
        Verdict(caseName, listed, $"the Pylon Workbench offers {objectName}");
        return listed ? 1 : 0;
    }

    private void SkipRecipeCases(string why, bool skipHost = true)
    {
        foreach (string name in RecipeCaseNames)
        {
            if (!skipHost && name == RecipeCaseNames[0])
            {
                continue;  // already judged against the vanilla bench
            }

            Skip(name, why);
        }
    }

    /// The recipe list of whichever prefab of `owner` offers the most, with the two numbers that
    /// decide whether the crafting window ever draws it (research.md chapter 18).
    ///
    /// The most-offering prefab rather than the first one: an object can have several prefabs and
    /// genassets.py deliberately authors the recipes onto one of them, so "the first" would be a
    /// coin flip. Every prefab is logged either way, as the injection system's Report does, so a
    /// failure is diagnosed off Player.log instead of guessed at.
    private bool TryReadRecipes(
        ObjectID owner, out List<ObjectID> recipes, out int slots, out int categories)
    {
        recipes = null;
        slots = 0;
        categories = 0;

        ref var infos = ref database.Value.objectInfos;
        var em = EntityManager;

        for (int i = 0; i < infos.Length; i++)
        {
            ref var info = ref infos[i];
            if (info.objectID != owner)
            {
                continue;
            }

            for (int p = 0; p < info.prefabEntities.Length; p++)
            {
                Entity prefab = info.prefabEntities[p];
                if (!em.Exists(prefab) || !em.HasBuffer<CanCraftObjectsBuffer>(prefab))
                {
                    continue;
                }

                DynamicBuffer<CanCraftObjectsBuffer> buffer =
                    em.GetBuffer<CanCraftObjectsBuffer>(prefab);

                var offered = new List<ObjectID>();
                for (int r = 0; r < buffer.Length; r++)
                {
                    // ObjectID.None is an authored-but-empty slot, not a recipe.
                    if (buffer[r].objectID != ObjectID.None)
                    {
                        offered.Add(buffer[r].objectID);
                    }
                }

                int included = em.HasBuffer<IncludedCraftingBuildingsBuffer>(prefab)
                    ? em.GetBuffer<IncludedCraftingBuildingsBuffer>(prefab).Length
                    : 0;

                Debug.Log($"[NBZTEST] recipes on {owner} prefab {p}: slots={buffer.Length}, "
                          + $"offers={offered.Count}, categories={included}");

                if (recipes == null || offered.Count > recipes.Count)
                {
                    recipes = offered;
                    slots = buffer.Length;
                    categories = included;
                }
            }
        }

        return recipes != null;
    }

    // ------------------------------------------------------------------------------------- steps

    private void WaitForPylon()
    {
        EnsureAreaAnchor();

        NativeArray<int2> pylons = _registry.Positions;
        if (pylons.Length == 0)
        {
            _framesWaitingForPylon++;

            // Retried rather than done once: the anchor above asks the game to stream the area in,
            // and that takes an unknown number of frames. Until it lands there is no ground to
            // build on and nothing to test.
            // Every ten seconds, not every second: registration is not instant — the first run of
            // this built 43 pylons before the registry reported one, and every extra pylon projects
            // a square that invalidates the control cases.
            //
            // AND NEVER WHILE A PYLON ENTITY ALREADY EXISTS, switched on or not. The condition above
            // reads the registry, which lists switched-ON pylons only, so a pylon we just built and
            // whose variation has not settled yet is invisible to it — and we build another, and
            // another. That is the "43 pylons" path, and left alone it is how a world ends up with
            // 367 of them.
            if (_framesWaitingForPylon >= FramesBeforeSelfProvisioning
                && _framesWaitingForPylon % 600 == 0
                && _spawnAttempt < 6
                && !AnyPylonEntityExists())
            {
                ProbeAndBuildPylon();
            }
            else if (_framesWaitingForPylon > SetupTimeoutFrames)
            {
                Debug.Log("[NBZTEST] SETUP FAIL no switched-on pylon, and building one did not take");
                RemoveOurOwnPylon();
                _step = FinalStep + 1;
                _framesUntilRerun = FramesBetweenRuns;
            }

            return;
        }

        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneConfig.ProtectionDiameter);
        int2 pylon = pylons[0];

        // Both probes have to sit on bare ground, so they are searched for rather than assumed:
        // one well inside the square, one well outside it. A fixed offset lands in a wall as often
        // as not, and a wall is not something a floor can be laid on.
        var tiles = CreateTileAccessor();

        if (!TryFindGround(tiles, pylon, 1, radius - 1, out _inside)
            || !TryFindUncoveredGround(tiles, pylons, pylon, radius, out _outside))
        {
            // Not a failure yet — the map streams in around a player over several seconds, and the
            // pylon now registers on the first attempt, so this step can easily arrive before the
            // tiles it needs exist. Keep looking; the run only gives up at the overall timeout.
            _framesWaitingForPylon++;

            if (!_announcedNoProbes)
            {
                _announcedNoProbes = true;
                Debug.Log("[NBZTEST] pylon found; waiting for enough map around it to pick probes");
            }

            if (_framesWaitingForPylon > SetupTimeoutFrames)
            {
                Debug.Log("[NBZTEST] SETUP FAIL never found bare ground both inside and outside "
                          + $"the square around ({pylon.x},{pylon.y})");
                RemoveOurOwnPylon();
                _step = FinalStep + 1;
                _framesUntilRerun = FramesBetweenRuns;
            }

            return;
        }

        _pylon = pylon;
        _tileset = tiles.GetTop(_inside).tileset;

        // A WALL IS THE ONE TILE THIS TEST CAN TRUST. A floor laid by the test only counts as
        // evidence if it can be damaged at all, and whether a given tileset even has a damageable
        // floor object is not something the test controls — the first run outside a protected square
        // refused to break, which would have made the inside result meaningless. Walls are what
        // bombs are for, so they answer the same question without that doubt.
        _haveWalls = TryFindTile(tiles, pylon, 1, radius - 1, 0, TileType.wall, out _insideWall)
                     && TryFindUncoveredTile(tiles, pylons, pylon, radius, TileType.wall, out _outsideWall);

        if (_haveWalls)
        {
            Debug.Log($"[NBZTEST] walls: inside=({_insideWall.x},{_insideWall.y}) "
                      + $"outside=({_outsideWall.x},{_outsideWall.y})");
        }

        Debug.Log($"[NBZTEST] pylon at ({pylon.x},{pylon.y}) radius={radius} "
                  + $"inside=({_inside.x},{_inside.y}) outside=({_outside.x},{_outside.y}) "
                  + $"tileset={_tileset}");

        Advance();
    }

    /// Builds a switched-on pylon so the test can run with nobody in the world, and says out loud
    /// what the map looks like where it is building. On a dedicated server with no players there is
    /// no character to place one, and no guarantee that any part of the map is streamed in — if the
    /// probe reports `none` for the ground, that is the answer to why nothing else works, and it is
    /// better to read it in the log than to infer it from six failed cases.
    private void ProbeAndBuildPylon()
    {
        var tiles = CreateTileAccessor();

        // Bare ground, not a wall. Two things make this a search rather than a constant: the first
        // attempt built at a fixed offset, landed inside a wall, and the game deleted the pylon
        // before the registry saw it; and an unloaded tile reads as `wall` too
        // (TileAccessor.DefaultTile), so "no ground anywhere" usually means "not streamed in yet".
        if (!TryFindGround(tiles, int2.zero, 0, SearchRadius, _spawnAttempt, out int2 at))
        {
            if (!_announcedNoGround)
            {
                _announcedNoGround = true;
                Debug.Log($"[NBZTEST] waiting for the map: nothing but wall within {SearchRadius} "
                          + "tiles of the origin, which is also what an unloaded chunk reads as");
            }

            return;
        }

        Debug.Log($"[NBZTEST] building at ({at.x},{at.y}), top={tiles.GetTopType(at)}");

        ObjectID pylonId = API.Authoring.GetObjectID(NoBreakZonePylonRegistrySystem.PylonObjectName);
        if (pylonId == ObjectID.None)
        {
            Debug.Log("[NBZTEST] SETUP FAIL the object database does not know the pylon yet");
            return;
        }

        // Report on the previous attempt before making another one. A pylon that vanishes between
        // retries was refused by the game's placement validation; one that survives but never shows
        // up in the registry is switched off, and those two need opposite fixes.
        if (_lastSpawned != Entity.Null)
        {
            Debug.Log(EntityManager.Exists(_lastSpawned)
                ? "[NBZTEST] the previous pylon still exists but did not register as switched on"
                : "[NBZTEST] the previous pylon was destroyed — the game refused that placement");
        }

        Entity pylon = EntityUtility.CreateEntity(
            World, new Vector3(at.x, 0f, at.y), pylonId, 1, database,
            NoBreakZonePylonGraphics.VariationOn);

        if (pylon == Entity.Null)
        {
            Debug.Log("[NBZTEST] SETUP FAIL could not create a pylon entity");
            return;
        }

        // Asking for variation 1 selects the switched-on PREFAB, but PugDatabase falls back to
        // variation 0 when it has no entry for the one asked for (research.md chapter 20), and the
        // registry decides on/off from this field rather than from which prefab was used. Setting it
        // outright is the difference between a pylon that projects a square and one that does not.
        ObjectDataCD data = EntityManager.GetComponentData<ObjectDataCD>(pylon);
        int spawnedVariation = data.variation;
        data.variation = NoBreakZonePylonGraphics.VariationOn;
        EntityManager.SetComponentData(pylon, data);

        _lastSpawned = pylon;
        _spawnAttempt++;

        Debug.Log($"[NBZTEST] built a pylon at ({at.x},{at.y}), prefab variation={spawnedVariation}, "
                  + $"switched to {NoBreakZonePylonGraphics.VariationOn} (attempt {_spawnAttempt})");
    }

    /// Lays a floor at both probes so the explosion has something of ours to destroy. A floor is the
    /// object the bug was reported against: it disappeared and dropped as an item.
    private void LayTestFloors()
    {
        AddTile(_inside, TileType.floor);
        AddTile(_outside, TileType.floor);
        Advance();
    }

    private void HitFloorsLikeAnExplosion()
    {
        var tiles = CreateTileAccessor();
        if (tiles.GetTopType(_inside) != TileType.floor || tiles.GetTopType(_outside) != TileType.floor)
        {
            // The tileset under the pylon may have no floor variant. Skipping beats reporting a
            // failure that says nothing about the mod.
            Skip("floor-inside-explosion", "this tileset has no floor tile to lay");
            Skip("floor-outside-explosion", "same");
            _floorIsDamageable = false;
            _step = 4;
            _wait = FramesBetweenSteps;
            return;
        }

        DamageTile(_inside, ExplosionDamage, explosionShaped: true);
        DamageTile(_outside, ExplosionDamage, explosionShaped: true);

        if (_haveWalls)
        {
            DamageTile(_insideWall, ExplosionDamage, explosionShaped: true);
            DamageTile(_outsideWall, ExplosionDamage, explosionShaped: true);
        }

        // Sixty frames, not the usual twelve. On 2026-09-09 wall-outside-explosion failed here and
        // wall-outside-pickaxe passed LATER ON THE SAME TILE, which can only mean the explosion had
        // killed it and this check looked before the game finished. A control that loses a race
        // reads exactly like a control that is broken, and it costs a session to tell apart.
        Advance(60);
    }

    private void CheckExplosionResult()
    {
        var tiles = CreateTileAccessor();

        // THE WALL PAIR IS THE LOAD-BEARING RESULT. A wall is unambiguously damageable, so its
        // control breaking is what makes its protected twin surviving mean something.
        if (_haveWalls)
        {
            bool outsideWallBroke = tiles.GetTopType(_outsideWall) != TileType.wall;

            Verdict("wall-outside-explosion", outsideWallBroke,
                "a wall outside every square still breaks — otherwise nothing below means anything");

            if (outsideWallBroke)
            {
                // THE REPORTED BUG, on a tile whose damageability was just demonstrated.
                Verdict("wall-inside-explosion",
                    tiles.GetTopType(_insideWall) == TileType.wall,
                    "a wall inside the square survives an explosion");
            }
            else
            {
                Skip("wall-inside-explosion", "the control did not break, so this proves nothing");
            }
        }
        else
        {
            Skip("wall-outside-explosion", "no wall found both inside and outside the square");
            Skip("wall-inside-explosion", "same");
        }

        // The floor pair, same shape. A floor the test lays itself is only evidence if it can be
        // damaged at all — whether a given tileset has a damageable floor object is not something
        // this test controls, and on the first server run it did not.
        bool outsideFloorBroke = tiles.GetTopType(_outside) != TileType.floor;

        _floorIsDamageable = outsideFloorBroke;

        if (outsideFloorBroke)
        {
            Verdict("floor-inside-explosion",
                tiles.GetTopType(_inside) == TileType.floor,
                "the floor inside the square survives an explosion");
            Verdict("floor-outside-explosion", true,
                "the floor outside every square still breaks");
        }
        else
        {
            Skip("floor-inside-explosion",
                $"tileset {_tileset}'s floor did not break outside the square either, so surviving "
                + "inside it proves nothing");
            Skip("floor-outside-explosion", "same");
        }

        Advance();
    }

    private void HitInsideLikeAPickaxe()
    {
        AddTile(_inside, TileType.floor);
        DamageTile(_inside, PickaxeDamage, explosionShaped: false);
        Advance();
    }

    private void CheckPickaxeResult()
    {
        // Same oracle as the explosion pair: a floor that cannot be damaged anywhere proves nothing
        // by surviving here either. The explosion step already established which it is.
        if (!_floorIsDamageable)
        {
            Skip("floor-inside-pickaxe",
                "this tileset's floor is not damageable, so surviving proves nothing");
            Advance();
            return;
        }

        Verdict("floor-inside-pickaxe",
            CreateTileAccessor().GetTopType(_inside) == TileType.floor,
            "the floor inside the square still survives capped damage");
        Advance();
    }

    /// The tilemap-editing half: an explosion also turns bare ground into dug-up ground by writing
    /// the tile buffer directly, with no damage involved at all.
    private void RequestDigs()
    {
        AddTile(_inside, TileType.dugUpGround);
        AddTile(_outside, TileType.dugUpGround);
        Advance();
    }

    private void CheckDigResult()
    {
        var tiles = CreateTileAccessor();

        Verdict("dig-inside",
            !tiles.HasType(_inside, TileType.dugUpGround),
            "digging inside the square is refused");

        Verdict("dig-outside",
            tiles.HasType(_outside, TileType.dugUpGround),
            "digging outside every square still works");

        Advance();
    }

    /// A Clear followed by an Add at the same tile is how the game levels ground under a newly
    /// placed object. Refusing only the Add would leave a hole — worse than the digging the filter
    /// is there to prevent — so the pair has to survive intact.
    private void RequestClearThenAdd()
    {
        var buffer = EntityManager.GetBuffer<TileUpdateBuffer>(tileUpdateBufferSingletonEntity);
        buffer.Add(new TileUpdateBuffer { command = TileUpdateBuffer.Command.Clear, position = _inside });
        buffer.Add(new TileUpdateBuffer
        {
            command = TileUpdateBuffer.Command.Add,
            position = _inside,
            tile = new TileCD { tileset = _tileset, tileType = TileType.dugUpGround },
        });

        Advance();
    }

    private void CheckClearThenAddResult()
    {
        Verdict("clear-then-add",
            CreateTileAccessor().GetTopType(_inside) != TileType.none,
            "a Clear+Add pair inside the square does not empty the tile");
        Advance();
    }

    /// ORE INSIDE A PROTECTED SQUARE IS SUPPOSED TO STAY PUT, AND THIS CASE USED TO SAY THE
    /// OPPOSITE. It failed in game for three worlds running and was reported as a resource
    /// duplication bug. It was neither — the expectation was wrong, and here is why.
    ///
    /// Pickaxe ore is not a thing standing on a tile. It is a resource CONTAINED IN A WALL, and the
    /// entity holding the health at that position is the wall:
    ///
    ///     if (tileType.IsContainedResource()
    ///         && tileAccessor.GetType(position, TileType.wall, out var tileCD))   // PlayerController
    ///
    /// Walls have been protected since design.md's 2026-08-07 decision, so a protected wall means
    /// the ore inside it does not come out either. The mod never protects ore itself and could not
    /// if it wanted to: all ten ore tiles in object_flags.csv have health = 0, and
    /// NoBreakZoneProtectionRule returns false on its first line for anything without health. That
    /// is why no PROTECT line for ore ever appeared in the log.
    ///
    /// AND IT IS NOT RESOURCE DUPLICATION. Duplication needs something that pays out while refusing
    /// to die. Pickaxe ore is lootOnDmg=0 — hitting it yields nothing, so protecting it yields
    /// nothing forever. The one that pays out per hit is the DRILL's boulder, lootOnDmg=1, and
    /// boulder-inside-is-not-protected below is the case that guards it.
    ///
    /// A human confirmed the behaviour is what they want (2026-09-10): ore inside your base stays
    /// there while the pylon is on, exactly like the wall around it.
    private void BlowUpOre()
    {
        var tiles = CreateTileAccessor();
        NativeArray<int2> pylons = _registry.Positions;
        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneConfig.ProtectionDiameter);

        _haveOre = TryFindTile(tiles, _pylon, 1, radius - 1, 0, TileType.ore, out _insideOre)
                   && TryFindUncoveredTile(tiles, pylons, _pylon, radius, TileType.ore, out _outsideOre);

        if (!_haveOre)
        {
            Skip("ore-inside-is-protected", "no ore both inside and outside the square here");
            Skip("ore-outside-still-breaks", "same");
            Advance();
            return;
        }

        Debug.Log($"[NBZTEST] ore: inside=({_insideOre.x},{_insideOre.y}) "
                  + $"outside=({_outsideOre.x},{_outsideOre.y})");

        DamageTile(_insideOre, ExplosionDamage, explosionShaped: true);
        DamageTile(_outsideOre, ExplosionDamage, explosionShaped: true);
        Advance();
    }

    private void CheckOreResult()
    {
        if (!_haveOre)
        {
            Advance();
            return;
        }

        var tiles = CreateTileAccessor();
        bool outsideGone = tiles.GetTopType(_outsideOre) != TileType.ore;

        Verdict("ore-outside-still-breaks", outsideGone,
            "ore outside every square breaks — the control for the case below");

        if (outsideGone)
        {
            bool insideGone = tiles.GetTopType(_insideOre) != TileType.ore;

            // A red here says the rule and the game disagree, and the rule is not the interesting
            // half — NoBreakZoneProtectionRule excludes ore outright and 2278 objects are checked
            // against it offline. What is worth knowing is what the mod is looking at when it
            // decides, so say it rather than leaving the next session to guess. Guessing is what
            // cost this project three play sessions on one sprite (research.md chapter 20).
            // Diagnosis on the failing side, which is now the side that BREAKS. If ore inside a
            // protected square comes out, the wall holding it went with it, and that is the
            // interesting failure.
            if (insideGone)
            {
                Debug.Log("[NBZTEST] ore diagnosis @inside — " + DescribeEntityAt(_insideOre));
                Debug.Log("[NBZTEST] ore diagnosis @outside(control) — " + DescribeEntityAt(_outsideOre));
            }

            Verdict("ore-inside-is-protected",
                !insideGone,
                "ore inside the square stays put — it sits in a wall, and the wall is protected");
        }
        else
        {
            Skip("ore-inside-is-protected", "the control did not break, so this proves nothing");
        }

        Advance();
    }

    /// The open question this mod has carried since walls joined the protected set: a wall inside a
    /// square survives an explosion, but does it survive a pickaxe? Capped damage needs several
    /// swings, so this delivers them one per visit rather than all at once — the per-hit cap is the
    /// whole point of the difference.
    private void MineTheWalls()
    {
        if (!_haveWalls)
        {
            Skip("wall-inside-pickaxe", "no wall found both inside and outside the square");
            Skip("wall-outside-pickaxe", "same");
            Advance();
            return;
        }

        DamageTile(_insideWall, PickaxeDamage, explosionShaped: false);
        DamageTile(_outsideWall, PickaxeDamage, explosionShaped: false);

        if (++_pickaxeSwings < PickaxeSwings)
        {
            _wait = 4;
            return;
        }

        Advance();
    }

    private void CheckWallPickaxeResult()
    {
        if (!_haveWalls)
        {
            Advance();
            return;
        }

        var tiles = CreateTileAccessor();
        bool outsideGone = tiles.GetTopType(_outsideWall) != TileType.wall;

        Verdict("wall-outside-pickaxe", outsideGone,
            $"a wall outside every square falls to {PickaxeSwings} capped hits");

        if (outsideGone)
        {
            Verdict("wall-inside-pickaxe",
                tiles.GetTopType(_insideWall) == TileType.wall,
                "and a wall inside the square does not");
        }
        else
        {
            Skip("wall-inside-pickaxe",
                $"{PickaxeSwings} capped hits did not fell the control wall either");
        }

        Advance();
    }

    /// design.md §6's other half: "to change the base, switch the pylon off". Protection that cannot be
    /// switched off is a trap, so the release path is worth as much as the protection itself.
    private void SwitchPylonOff()
    {
        if (!_haveWalls || !TryGetPylonEntity(out Entity pylon))
        {
            Skip("release-on-switch-off", "no pylon entity to switch off");
            _step = PlaceableStep;
            return;
        }

        ObjectDataCD data = EntityManager.GetComponentData<ObjectDataCD>(pylon);
        data.variation = NoBreakZonePylonGraphics.VariationOff;
        EntityManager.SetComponentData(pylon, data);

        Debug.Log("[NBZTEST] switched the pylon off");
        Advance(60);
    }

    private void BlowUpTheReleasedWall()
    {
        // A wall inside the square that survived an explosion a moment ago. With the pylon off it
        // has to be destructible again.
        DamageTile(_insideWall, ExplosionDamage, explosionShaped: true);
        Advance();
    }

    private void CheckReleaseResult()
    {
        Verdict("release-on-switch-off",
            CreateTileAccessor().GetTopType(_insideWall) != TileType.wall,
            "switching the pylon off makes a protected wall breakable again");

        // Leave the world as it was found, so the next run starts from a switched-on pylon.
        if (TryGetPylonEntity(out Entity pylon))
        {
            ObjectDataCD data = EntityManager.GetComponentData<ObjectDataCD>(pylon);
            data.variation = NoBreakZonePylonGraphics.VariationOn;
            EntityManager.SetComponentData(pylon, data);
            Debug.Log("[NBZTEST] switched the pylon back on");
        }

        Advance();
    }

    /// Any pylon at all, on or off. Deliberately not the registry: that publishes switched-on
    /// pylons only, and "there is no pylon" is a different question from "no pylon is projecting a
    /// square" precisely while one we just built is still settling.
    private bool AnyPylonEntityExists()
    {
        EntityQuery query = EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<NoBreakZonePylonCD>());

        return !query.IsEmpty;
    }

    private bool TryGetPylonEntity(out Entity pylon)
    {
        EntityQuery query = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NoBreakZonePylonCD>());
        using NativeArray<Entity> found = query.ToEntityArray(Allocator.Temp);

        if (found.Length == 0)
        {
            pylon = Entity.Null;
            return false;
        }

        pylon = found[0];
        return true;
    }

    /// The other half of the mod, and the half every case above ignores. A chest or a workbench is
    /// not a tile: it is protected through IndestructibleCD and DontDestroyOnZeroHealthCD on a
    /// persistent entity, not through a damage entity conjured up when something hits a tilemap
    /// square. Humans confirmed this by hand in earlier sessions; nothing checked it automatically.
    private void SpawnPlaceables()
    {
        ObjectID benchId = API.Authoring.GetObjectID("WoodenWorkBench");
        if (benchId == ObjectID.None)
        {
            Skip("placeable-inside-survives", "the database does not know WoodenWorkBench");
            Skip("placeable-outside-breaks", "same");
            _step = PylonInvulnStep;
            return;
        }

        _insidePlaceable = EntityUtility.CreateEntity(
            World, new Vector3(_inside.x, 0f, _inside.y), benchId, 1, database);
        _outsidePlaceable = EntityUtility.CreateEntity(
            World, new Vector3(_outside.x, 0f, _outside.y), benchId, 1, database);

        if (_insidePlaceable == Entity.Null || _outsidePlaceable == Entity.Null)
        {
            Skip("placeable-inside-survives", "could not spawn a workbench");
            Skip("placeable-outside-breaks", "same");
            _step = PylonInvulnStep;
            return;
        }

        Debug.Log($"[NBZTEST] placed a workbench inside ({_inside.x},{_inside.y}) and outside "
                  + $"({_outside.x},{_outside.y})");

        // Long enough for the protection system to see them and for the game to reject either
        // placement if it wants to.
        Advance(60);
    }

    /// Damage written straight into HealthChangeBuffer, which is what a mob, a boss or an
    /// environmental hazard ends up doing (research.md chapter 8). It bypasses IndestructibleCD —
    /// that one only guards the player's own predicted mining — so this exercises the OTHER
    /// component the mod relies on, the destroy gate.
    private void DamagePlaceables()
    {
        if (!EntityManager.Exists(_insidePlaceable) || !EntityManager.Exists(_outsidePlaceable))
        {
            Skip("placeable-inside-survives", "a workbench did not survive being placed at all");
            Skip("placeable-outside-breaks", "same");
            _step = PylonInvulnStep;
            return;
        }

        DamageEntity(_insidePlaceable, ExplosionDamage);
        DamageEntity(_outsidePlaceable, ExplosionDamage);

        Advance(60);
    }

    private void CheckPlaceableResult()
    {
        bool outsideGone = IsDestroyed(_outsidePlaceable);

        Verdict("placeable-outside-breaks", outsideGone,
            "a workbench outside every square is destroyed — the control");

        if (outsideGone)
        {
            Verdict("placeable-inside-survives",
                !IsDestroyed(_insidePlaceable),
                "and one inside the square survives the same damage");
        }
        else
        {
            Skip("placeable-inside-survives", "the control survived too, so this proves nothing");
        }

        // Clean up whatever is left so runs do not litter the world with workbenches.
        foreach (Entity e in new[] { _insidePlaceable, _outsidePlaceable })
        {
            if (EntityManager.Exists(e))
            {
                EntityManager.DestroyEntity(e);
            }
        }

        Advance();
    }

    /// design.md:320 — "While switched on, the pylon is invulnerable. It is not destroyed by
    /// explosions, by pickaxes or by mob attacks" — and the line after it says why: if the pylon
    /// falls, every square it was projecting falls with it, so this is the case that stands under
    /// all the others.
    ///
    /// THE CONTROL IS A SECOND PYLON, SWITCHED OFF. "A workbench outside the square dies" would
    /// prove nothing here: it says nothing about whether a pylon left unguarded would have died.
    /// Only the same object with the switch in the other position answers that. It also means this
    /// case brushes design.md §5 on the way past — a switched-off pylon is an ordinary object.
    ///
    /// Standing a second pylon in the world is safe precisely because it is off: the registry
    /// collects positions only from pylons at VariationOn, so this one is not in Positions and
    /// projects no square of its own. That matters — an earlier version of this test managed to
    /// build 43 pylons and invalidate every control it had.
    private void SpawnControlPylon()
    {
        // Captured by position, not by "whichever pylon the query returns first". In a moment there
        // will be two, and the later steps must not be able to confuse them.
        if (!TryGetPylonEntityAt(_pylon, out _protectedPylon))
        {
            Skip("pylon-on-survives", "no switched-on pylon entity to damage");
            Skip("pylon-off-breaks", "same");
            _step = BoulderStep;
            return;
        }

        ObjectID pylonId = API.Authoring.GetObjectID(NoBreakZonePylonRegistrySystem.PylonObjectName);
        if (pylonId == ObjectID.None)
        {
            Skip("pylon-on-survives", "the object database does not know the pylon");
            Skip("pylon-off-breaks", "same");
            _step = BoulderStep;
            return;
        }

        _controlPylon = EntityUtility.CreateEntity(
            World, new Vector3(_outside.x, 0f, _outside.y), pylonId, 1, database,
            NoBreakZonePylonGraphics.VariationOff);

        if (_controlPylon == Entity.Null)
        {
            Skip("pylon-off-breaks", "could not spawn a second pylon as the control");
            Skip("pylon-on-survives", "same");
            _step = BoulderStep;
            return;
        }

        // Set outright for the same reason ProbeAndBuildPylon does: the registry decides on/off from
        // this field, not from which prefab the database handed back.
        ObjectDataCD data = EntityManager.GetComponentData<ObjectDataCD>(_controlPylon);
        data.variation = NoBreakZonePylonGraphics.VariationOff;
        EntityManager.SetComponentData(_controlPylon, data);

        Debug.Log($"[NBZTEST] control pylon (switched off) at ({_outside.x},{_outside.y}), "
                  + $"protected pylon at ({_pylon.x},{_pylon.y})");

        // Long enough for the registry to classify both and for the protection to settle on each.
        Advance(60);
    }

    /// Damage written straight into HealthChangeBuffer — a mob, a boss, an explosion or the
    /// environment all end up here (research.md chapter 8). It is also the only shape of damage this test
    /// can aim at a pylon at all, since the pickaxe path is client-predicted, and it is exactly the
    /// path IndestructibleCD does NOT guard. Before the fix that came with this case, the switched-on
    /// pylon died right here.
    private void DamagePylons()
    {
        if (!EntityManager.Exists(_protectedPylon) || !EntityManager.Exists(_controlPylon))
        {
            Skip("pylon-off-breaks", "a pylon stopped existing before it could be damaged");
            Skip("pylon-on-survives", "same");
            _step = BoulderStep;
            return;
        }

        DamageEntity(_protectedPylon, ExplosionDamage);
        DamageEntity(_controlPylon, ExplosionDamage);

        Advance(60);
    }

    private void CheckPylonResult()
    {
        bool controlGone = IsDestroyed(_controlPylon);

        Verdict("pylon-off-breaks", controlGone,
            "a switched-off pylon is destroyed by explosion-sized damage — the control");

        if (controlGone)
        {
            Verdict("pylon-on-survives",
                !IsDestroyed(_protectedPylon),
                "and a switched-on one shrugs off the same damage (design.md:320)");
        }
        else
        {
            Skip("pylon-on-survives", "the control survived too, so this proves nothing");
        }

        // Never leave the control standing: switched off it is harmless, but a stray pylon somebody
        // later switches on would move every square the next run measures against.
        if (EntityManager.Exists(_controlPylon))
        {
            EntityManager.DestroyEntity(_controlPylon);
        }

        _controlPylon = Entity.Null;

        Advance();
    }

    /// design.md §6 names this exact scenario as the worst thing this mod could do:
    ///
    ///   "The drill is a protection target, but the ore boulder is not. If the implementation cannot
    ///    tell the two apart and ends up protecting the boulder as well, the boulder never depletes
    ///    and ore comes out endlessly."
    ///
    /// A protected boulder is an infinite resource: the drill keeps mining, the boulder never
    /// depletes, and the save's economy is broken in a way no later fix can undo. Nothing checked it
    /// in game until now.
    ///
    /// SPAWNED, NOT SEARCHED FOR. The ore-tile pair beside this one has to find ore both inside and
    /// outside the square, and reported SKIP on three worlds running. A boulder is an entity, not a
    /// tile (object_flags.csv: PlaceablePrefab, health=1, requiresDrill=1, tileCD=0), so it can be
    /// placed exactly like the workbench and never depends on terrain luck.
    ///
    /// IT ASKS ABOUT PROTECTION, NOT DESTRUCTION, AND THE FIRST VERSION GOT THAT WRONG. It damaged
    /// the boulder and expected it to die, which failed — a boulder is damageable=0 and
    /// requiresDrill=1, so it is DEPLETED BY A DRILL rather than destroyed by damage, and hitting it
    /// drops loot instead of killing it. "Big damage kills it" was never a valid stand-in.
    ///
    /// The claim design.md §6 actually makes is that the mod must not PROTECT it — "the boulder
    /// never depletes and ore comes out endlessly" — so that is what gets measured, straight off the
    /// component the mod would have added.
    private void SpawnBoulders()
    {
        ObjectID boulderId = API.Authoring.GetObjectID(BoulderObjectName);
        if (boulderId == ObjectID.None)
        {
            Skip("boulder-inside-is-not-protected", $"the database does not know {BoulderObjectName}");
            _step = ScenarioStep;
            return;
        }

        _insideBoulder = EntityUtility.CreateEntity(
            World, new Vector3(_inside.x, 0f, _inside.y), boulderId, 1, database);
        _outsideBoulder = EntityUtility.CreateEntity(
            World, new Vector3(_outside.x, 0f, _outside.y), boulderId, 1, database);

        if (_insideBoulder == Entity.Null || _outsideBoulder == Entity.Null)
        {
            Skip("boulder-inside-is-not-protected", "could not spawn a boulder");
            _step = ScenarioStep;
            return;
        }

        Debug.Log($"[NBZTEST] placed a {BoulderObjectName} inside ({_inside.x},{_inside.y}) and "
                  + $"outside ({_outside.x},{_outside.y})");

        // Long enough for the protection system to judge them — which is the whole question here.
        Advance(60);
    }

    /// Nothing is damaged here, which is the correction. The old version hit the boulders and
    /// expected them to die; they do not, because a boulder is depleted by a drill rather than
    /// destroyed. Waiting is all this step does — the protection system needs a frame or two to
    /// judge the pair.
    private void SettleBoulders()
    {
        if (!EntityManager.Exists(_insideBoulder) || !EntityManager.Exists(_outsideBoulder))
        {
            Skip("boulder-inside-is-not-protected", "a boulder did not survive being placed at all");
            _step = ScenarioStep;
            return;
        }

        Advance(60);
    }

    private void CheckBoulderResult()
    {
        // Straight off the component the mod would have added. NoBreakZoneProtectedCD means "we made
        // this indestructible"; on a drill's boulder that is the one failure design.md §6 puts above
        // every other property of this mod, because the drill would then mine it forever.
        bool claimed = EntityManager.Exists(_insideBoulder)
                       && EntityManager.HasComponent<NoBreakZoneProtectedCD>(_insideBoulder);

        bool guarded = EntityManager.Exists(_insideBoulder)
                       && EntityManager.HasComponent<DontDestroyOnZeroHealthCD>(_insideBoulder)
                       && !EntityManager.GetComponentData<DontDestroyOnZeroHealthCD>(_insideBoulder)
                               .disabled;

        if (claimed || guarded)
        {
            Debug.Log("[NBZTEST] boulder diagnosis @inside — " + DescribeEntityAt(_inside));
        }

        Verdict("boulder-inside-is-not-protected",
            !claimed && !guarded,
            "a drill's boulder inside the square is left unprotected — protecting it is the "
            + "resource duplication design.md §6 forbids outright");

        foreach (Entity e in new[] { _insideBoulder, _outsideBoulder })
        {
            if (EntityManager.Exists(e))
            {
                EntityManager.DestroyEntity(e);
            }
        }

        _insideBoulder = Entity.Null;
        _outsideBoulder = Entity.Null;

        Advance();
    }

    // ------------------------------------------------------------------------------- scenarios
    //
    // EVERY CASE ABOVE POKES AND LOOKS IN THE SAME BREATH, AND THAT IS WHY THEY ALL PASSED WHILE THE
    // MOD WAS BADLY BROKEN. A human switched a pylon on, mined their own base, switched it off, and
    // watched the whole base turn into items — the damage had been banked at zero health the whole
    // time, held up by one component that switching off removes.
    //
    // No case could see it. release-on-switch-off comes closest and still misses, because it applies
    // FRESH damage after switching off and then reports that the wall broke. It cannot tell "the
    // release worked" from "the release killed everything", and it says PASS either way.
    //
    // What these add is a gap. Hit something, then do nothing to it, then look. The interesting
    // failures of a mod that changes when things die are all in that gap.

    private void ScenarioSetUp()
    {
        var tiles = CreateTileAccessor();
        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneConfig.ProtectionDiameter);

        // A wall of its own, so the earlier cases' leftovers are not what gets measured.
        _haveScenarioWall = TryFindTile(tiles, _pylon, 1, radius - 1, 3, TileType.wall, out _scenarioWall);

        ObjectID benchId = API.Authoring.GetObjectID("WoodenWorkBench");
        if (benchId != ObjectID.None)
        {
            _scenarioChest = EntityUtility.CreateEntity(
                World, new Vector3(_inside.x, 0f, _inside.y), benchId, 1, database);
        }

        if (!_haveScenarioWall && _scenarioChest == Entity.Null)
        {
            SkipScenarios("no wall and no workbench to work with inside the square");
            _step = FinalStep;
            return;
        }

        Debug.Log($"[NBZTEST] scenario: wall={_haveScenarioWall} at ({_scenarioWall.x},{_scenarioWall.y}), "
                  + $"placeable={_scenarioChest != Entity.Null}");

        Advance(60);
    }

    /// Mine, and then stop. The stopping is the point.
    private void ScenarioMineAndLeaveAlone()
    {
        if (_haveScenarioWall)
        {
            DamageTile(_scenarioWall, PickaxeDamage, explosionShaped: false);
        }

        if (EntityManager.Exists(_scenarioChest))
        {
            DamageEntity(_scenarioChest, ExplosionDamage);
        }

        if (++_scenarioSwings < PickaxeSwings)
        {
            _wait = 4;
            return;
        }

        // Two full seconds of nothing at all. Anything that dies on a timer, or on the next tick of
        // some system we have not thought about, dies inside this window.
        Advance(120);
    }

    private void ScenarioCheckIdle()
    {
        if (_haveScenarioWall)
        {
            Verdict("damaged-wall-survives-idle",
                CreateTileAccessor().GetTopType(_scenarioWall) == TileType.wall,
                "a wall mined inside the square is still standing two seconds later, with nobody "
                + "touching it");
        }
        else
        {
            Skip("damaged-wall-survives-idle", "no wall inside the square to mine");
        }

        Advance();
    }

    /// The exact sequence a player performed on 2026-09-09: mine inside your base, then switch off.
    /// Nothing is hit after the switch — that is what separates this from release-on-switch-off.
    private void ScenarioSwitchOffAndWatch()
    {
        if (!TryGetPylonEntityAt(_pylon, out Entity pylon))
        {
            SkipScenarios("no pylon entity to switch off");
            _step = FinalStep;
            return;
        }

        ObjectDataCD data = EntityManager.GetComponentData<ObjectDataCD>(pylon);
        data.variation = NoBreakZonePylonGraphics.VariationOff;
        EntityManager.SetComponentData(pylon, data);

        Debug.Log("[NBZTEST] scenario: switched off without hitting anything first");
        Advance(60);
    }

    private void ScenarioCheckAfterSwitchOff()
    {
        if (_haveScenarioWall)
        {
            Verdict("damaged-wall-survives-switch-off",
                CreateTileAccessor().GetTopType(_scenarioWall) == TileType.wall,
                "switching the pylon off does not destroy what was mined while it was on — "
                + "protection has to be protection, not destruction deferred");
        }
        else
        {
            Skip("damaged-wall-survives-switch-off", "no wall inside the square to mine");
        }

        if (EntityManager.Exists(_scenarioChest) || _scenarioChest != Entity.Null)
        {
            Verdict("damaged-placeable-survives-switch-off",
                !IsDestroyed(_scenarioChest),
                "and the same for a workbench, which takes damage by a different route");
        }
        else
        {
            Skip("damaged-placeable-survives-switch-off", "no workbench was placed");
        }

        Advance();
    }

    /// Flip the switch repeatedly with damage in between. State machines that are right once are not
    /// always right the third time, and a player toggling a pylon while building is ordinary.
    private void ScenarioToggleCycle()
    {
        if (!TryGetPylonEntityAt(_pylon, out Entity pylon))
        {
            Skip("toggle-cycle-destroys-nothing", "no pylon entity to toggle");
            Skip("release-still-lets-you-mine", "same");
            _step = FinalStep;
            return;
        }

        ObjectDataCD data = EntityManager.GetComponentData<ObjectDataCD>(pylon);
        bool on = data.variation == NoBreakZonePylonGraphics.VariationOn;

        data.variation = on
            ? NoBreakZonePylonGraphics.VariationOff
            : NoBreakZonePylonGraphics.VariationOn;
        EntityManager.SetComponentData(pylon, data);

        // Hit it while switched on, so each cycle banks something new if banking is possible again.
        if (!on && _haveScenarioWall)
        {
            DamageTile(_scenarioWall, PickaxeDamage, explosionShaped: false);
        }

        if (++_toggleCycles < 6)
        {
            _wait = 30;
            return;
        }

        // Leave it OFF, which sets up the last case.
        data.variation = NoBreakZonePylonGraphics.VariationOff;
        EntityManager.SetComponentData(pylon, data);

        Debug.Log("[NBZTEST] scenario: toggled six times, left off");
        Advance(60);
    }

    private void ScenarioCheckToggleThenMine()
    {
        if (!_haveScenarioWall)
        {
            Skip("toggle-cycle-destroys-nothing", "no wall inside the square to mine");
            Skip("release-still-lets-you-mine", "same");
            CleanUpScenario();
            _step = FinalStep;
            return;
        }

        Verdict("toggle-cycle-destroys-nothing",
            CreateTileAccessor().GetTopType(_scenarioWall) == TileType.wall,
            "six on/off cycles with mining in between destroy nothing");

        // Now the control, with the pylon left off by the step before: hit it once more, for real.
        DamageTile(_scenarioWall, ExplosionDamage, explosionShaped: true);
        Advance(60);
    }

    /// THE CONTROL FOR EVERY SCENARIO ABOVE, and without it they are worth nothing: making
    /// everything permanently indestructible would score five greens. With the pylon off this wall
    /// has to break like any other. A red here means the health floor went too far and protection
    /// no longer lets go — which would be a worse bug than the one it fixed, because design.md §6's
    /// answer to "how do I change my base" is "switch the pylon off".
    private void ScenarioCheckRelease()
    {
        Verdict("release-still-lets-you-mine",
            CreateTileAccessor().GetTopType(_scenarioWall) != TileType.wall,
            "with the pylon off, that same wall breaks again — protection lets go");

        CleanUpScenario();
        Advance();
    }

    private void SkipScenarios(string why)
    {
        Skip("damaged-wall-survives-idle", why);
        Skip("damaged-wall-survives-switch-off", why);
        Skip("damaged-placeable-survives-switch-off", why);
        Skip("toggle-cycle-destroys-nothing", why);
        Skip("release-still-lets-you-mine", why);
    }

    private void CleanUpScenario()
    {
        if (EntityManager.Exists(_scenarioChest))
        {
            EntityManager.DestroyEntity(_scenarioChest);
        }

        _scenarioChest = Entity.Null;
    }

    /// The pylon standing on a given tile, rather than whichever one the query happens to return
    /// first. Once this test stands up a control there is more than one, and picking the wrong one
    /// would invert every verdict that follows.
    private bool TryGetPylonEntityAt(int2 tile, out Entity pylon)
    {
        EntityQuery query = EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<NoBreakZonePylonCD>(),
            ComponentType.ReadOnly<LocalTransform>());

        using NativeArray<Entity> found = query.ToEntityArray(Allocator.Temp);
        using NativeArray<LocalTransform> transforms =
            query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        for (int i = 0; i < found.Length; i++)
        {
            if (transforms[i].Position.RoundToInt2().Equals(tile))
            {
                pylon = found[i];
                return true;
            }
        }

        pylon = Entity.Null;
        return false;
    }

    /// Everything the protection decision turns on, for whatever entity is standing on a tile.
    ///
    /// Deliberately NOT the protection system's own query: that one excludes anything already
    /// carrying NoBreakZoneEvaluatedCD, and "was it judged and then never looked at again" is one of
    /// the two things this is here to tell apart. The other is which tile the game actually put an
    /// entity on — ore sits in walls, and a wall IS protected since design.md's 2026-08-07 decision,
    /// so an ore square whose entity reports tileType=wall would explain the failure completely.
    private string DescribeEntityAt(int2 tile)
    {
        EntityQuery query = EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<HealthCD>(),
            ComponentType.ReadOnly<ObjectDataCD>(),
            ComponentType.ReadOnly<LocalTransform>());

        using NativeArray<Entity> found = query.ToEntityArray(Allocator.Temp);
        using NativeArray<LocalTransform> transforms =
            query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        var em = EntityManager;

        for (int i = 0; i < found.Length; i++)
        {
            if (!transforms[i].Position.RoundToInt2().Equals(tile))
            {
                continue;
            }

            Entity e = found[i];
            HealthCD health = em.GetComponentData<HealthCD>(e);
            ObjectDataCD data = em.GetComponentData<ObjectDataCD>(e);

            string tilePart = em.HasComponent<TileCD>(e)
                ? $"tileType={em.GetComponentData<TileCD>(e).tileType}"
                : "no TileCD";

            string gate = em.HasComponent<DontDestroyOnZeroHealthCD>(e)
                ? $"dontDestroy.disabled={em.GetComponentData<DontDestroyOnZeroHealthCD>(e).disabled}"
                : "no DontDestroyOnZeroHealthCD";

            string indestructible = em.HasComponent<IndestructibleCD>(e)
                ? $"indestructible={em.IsComponentEnabled<IndestructibleCD>(e)}"
                : "no IndestructibleCD";

            return $"({tile.x},{tile.y}) objectID={data.objectID} {tilePart} "
                   + $"health={health.health}/{health.maxHealth} {gate} {indestructible} "
                   + $"ours={em.HasComponent<NoBreakZoneProtectedCD>(e)} "
                   + $"judged={em.HasComponent<NoBreakZoneEvaluatedCD>(e)}";
        }

        // Also an answer, and a useful one: a tile only becomes an entity while it is being damaged
        // (research.md chapter 21), so nothing here means the damage never reached it.
        return $"({tile.x},{tile.y}) no entity with health stands here";
    }

    private bool IsDestroyed(Entity entity)
    {
        if (!EntityManager.Exists(entity))
        {
            return true;
        }

        return EntityManager.HasComponent<EntityDestroyedCD>(entity)
               && EntityManager.IsComponentEnabled<EntityDestroyedCD>(entity);
    }

    /// The shared buffer every damage source in the game converges on. It lives on a system entity,
    /// hence IncludeSystems.
    private DynamicBuffer<HealthChangeBuffer> HealthChanges()
    {
        EntityQuery query = EntityManager.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<HealthChangeBuffer>() },
            Options = EntityQueryOptions.IncludeSystems,
        });

        return EntityManager.GetBuffer<HealthChangeBuffer>(query.GetSingletonEntity());
    }

    private void Finish()
    {
        RemoveOurOwnPylon();

        Debug.Log($"[NBZTEST] SUMMARY run={_run} pass={_pass} fail={_fail} skip={_skip}");
        Debug.Log("[NBZTEST] done — restart the server for another run, on terrain this one has not "
                  + "already destroyed");

        // Park past the end for good. Zero means the countdown in OnUpdate never fires, so nothing
        // re-arms this until the config flag is toggled or the server restarts.
        _step = FinalStep + 1;
        _framesUntilRerun = 0;
    }

    /// Takes back the pylon this run built, and only that one.
    ///
    /// WITHOUT THIS THE TEST POISONS ITS OWN WORLD. Nothing else ever removed a self-provisioned
    /// pylon, and a run happens every minute for as long as the server is up, so they accumulate in
    /// the save and survive into every later session. The 2026-09-08 run opened on a world holding
    /// 367 switched-on pylons, which is what made release-on-switch-off unanswerable: turning one
    /// off still leaves 366 covering the same wall.
    ///
    /// `_lastSpawned` is the whole safety argument. A pylon a human placed was never assigned to it,
    /// so this cannot take somebody's pylon away — and the next run simply builds itself a fresh one.
    private void RemoveOurOwnPylon()
    {
        if (_lastSpawned == Entity.Null || !EntityManager.Exists(_lastSpawned))
        {
            return;
        }

        EntityManager.DestroyEntity(_lastSpawned);
        Debug.Log("[NBZTEST] removed the pylon this run built");
        _lastSpawned = Entity.Null;
    }

    /// Resets everything a run owns, so a second run does not inherit the first one's verdicts or
    /// its idea of where the ground is.
    private void StartRun()
    {
        _run++;
        _step = 0;
        _wait = 0;
        _framesWaitingForPylon = 0;
        _pass = 0;
        _fail = 0;
        _skip = 0;
        _recipesJudged = false;
        _framesWaitingForRecipes = 0;
        _announcedNoGround = false;
        _announcedNoProbes = false;
        _floorIsDamageable = false;
        _haveOre = false;
        _pickaxeSwings = 0;
        _insidePlaceable = Entity.Null;
        _outsidePlaceable = Entity.Null;
        _protectedPylon = Entity.Null;
        _controlPylon = Entity.Null;
        _insideBoulder = Entity.Null;
        _outsideBoulder = Entity.Null;
        _scenarioChest = Entity.Null;
        _haveScenarioWall = false;
        _scenarioSwings = 0;
        _toggleCycles = 0;
        _lastSpawned = Entity.Null;
        _spawnAttempt = 0;

        Debug.Log($"[NBZTEST] ---- run {_run} starting ----");
    }

    // ---------------------------------------------------------------------------------- plumbing

    /// First tile of bare ground in the ring between minDistance and maxDistance from the origin,
    /// searched outwards so the answer is as close to the Core as possible.
    ///
    /// `ground` specifically, not "walkable": a floor can be laid on it, a pylon can stand on it, and
    /// the explosion cases need something ordinary underneath. Walls, water, pits and anything the
    /// world generator decorated with are all skipped.
    private static bool TryFindGround(
        TileAccessor tiles, int2 centre, int minDistance, int maxDistance, out int2 found)
    {
        return TryFindGround(tiles, centre, minDistance, maxDistance, 0, out found);
    }

    /// Bare ground that NO pylon covers.
    ///
    /// The control cases only mean something if the tile they use is genuinely unprotected, and
    /// "outside the first pylon's square" is not the same thing once there is more than one pylon —
    /// which a real base will have, and which this test managed to create 43 of on its first run.
    private static bool TryFindUncoveredGround(
        TileAccessor tiles, NativeArray<int2> pylons, int2 centre, int radius, out int2 found)
    {
        return TryFindUncoveredTile(tiles, pylons, centre, radius, TileType.ground, out found);
    }

    private static bool TryFindUncoveredTile(
        TileAccessor tiles, NativeArray<int2> pylons, int2 centre, int radius, TileType wanted,
        out int2 found)
    {
        var px = new int[pylons.Length];
        var pz = new int[pylons.Length];
        for (int i = 0; i < pylons.Length; i++)
        {
            px[i] = pylons[i].x;
            pz[i] = pylons[i].y;
        }

        for (int skip = 0; skip < 400; skip++)
        {
            if (!TryFindTile(tiles, centre, radius + 2, radius + 40, skip, wanted, out int2 candidate))
            {
                break;
            }

            if (!NoBreakZoneRange.AllTilesCovered(
                    px, pz, pylons.Length,
                    candidate.x, candidate.y, candidate.x, candidate.y, radius))
            {
                found = candidate;
                return true;
            }
        }

        found = int2.zero;
        return false;
    }

    /// <param name="skip">How many matches to pass over. Retrying with a bigger number walks to the
    /// next candidate instead of hammering a spot the game will not accept.</param>
    private static bool TryFindGround(
        TileAccessor tiles, int2 centre, int minDistance, int maxDistance, int skip, out int2 found)
    {
        return TryFindTile(tiles, centre, minDistance, maxDistance, skip, TileType.ground, out found);
    }

    private static bool TryFindTile(
        TileAccessor tiles, int2 centre, int minDistance, int maxDistance, int skip, TileType wanted,
        out int2 found)
    {
        for (int ring = math.max(minDistance, 0); ring <= maxDistance; ring++)
        {
            for (int x = -ring; x <= ring; x++)
            {
                for (int z = -ring; z <= ring; z++)
                {
                    // Only the edge of each ring — the inside was covered by a smaller one.
                    if (math.max(math.abs(x), math.abs(z)) != ring)
                    {
                        continue;
                    }

                    int2 candidate = centre + new int2(x, z);
                    if (tiles.GetTopType(candidate) == wanted)
                    {
                        if (skip > 0)
                        {
                            skip--;
                            continue;
                        }

                        found = candidate;
                        return true;
                    }
                }
            }
        }

        found = int2.zero;
        return false;
    }

    /// Pins the area around the origin in memory.
    ///
    /// A dedicated server with nobody connected streams nothing: every tile reads back as
    /// TileAccessor.DefaultTile, which is a wall, so the test has no map to work on. KeepAreaLoadedCD
    /// is the game's own answer — UnloadToSerializeWorldSystem collects every entity carrying it and
    /// keeps a circle around each one resident. One entity is enough, and it is thrown away with the
    /// world because nothing serialises it.
    private void EnsureAreaAnchor()
    {
        if (_areaAnchor != Entity.Null && EntityManager.Exists(_areaAnchor))
        {
            return;
        }

        _areaAnchor = EntityManager.CreateEntity(
            typeof(LocalTransform), typeof(KeepAreaLoadedCD), typeof(DontSerializeCD));

        EntityManager.SetComponentData(_areaAnchor, LocalTransform.FromPosition(float3.zero));
        EntityManager.SetComponentData(_areaAnchor, new KeepAreaLoadedCD
        {
            KeepLoadedRadius = KeepLoadedRadius,
            StartLoadRadius = KeepLoadedRadius + 10f,
            ImmediateLoadRadius = KeepLoadedRadius,
        });

        Debug.Log($"[NBZTEST] pinned a {KeepLoadedRadius}-tile area around the origin so the map loads");
    }

    private void Advance()
    {
        Advance(FramesBetweenSteps);
    }

    /// Advance and wait longer than the usual gap, for a step whose effect the game needs more than
    /// a few frames to apply.
    private void Advance(int wait)
    {
        _step++;
        _wait = wait;
    }

    private void Verdict(string name, bool passed, string what)
    {
        if (passed)
        {
            _pass++;
            Debug.Log($"[NBZTEST] {name} PASS — {what}");
            return;
        }

        _fail++;
        Debug.LogError($"[NBZTEST] {name} FAIL — expected: {what}");
    }

    private void Skip(string name, string why)
    {
        _skip++;
        Debug.Log($"[NBZTEST] {name} SKIP — {why}");
    }

    private void AddTile(int2 position, TileType tileType)
    {
        EntityManager.GetBuffer<TileUpdateBuffer>(tileUpdateBufferSingletonEntity).Add(
            new TileUpdateBuffer
            {
                command = TileUpdateBuffer.Command.Add,
                position = position,
                tile = new TileCD { tileset = _tileset, tileType = tileType },
            });
    }

    /// Explosion-shaped damage aimed at an ENTITY rather than at a tilemap square, in the shape the
    /// game actually accepts. The three flags are not decoration — with any of them missing this
    /// call silently does nothing at all, which is exactly what it did on 2026-09-08 and why both
    /// of its controls came back red (Editor/Docs/research.md chapter 22):
    ///
    ///   applyToNonPredicted  UpdateHealthFromBufferSystem computes
    ///                          flag = !applyToNonPredicted && has Simulate && !Simulate enabled
    ///                        and when flag is true it applies damage ONLY if health survives it
    ///                        (`health > -amount`), then `continue`s past the destroy path entirely.
    ///                        With -9999 that reads "health > 9999", so a 10 HP workbench takes
    ///                        nothing and can never be destroyed.
    ///   bypassMaxDamagePerHit  otherwise the amount is clamped to the target's maxDamagePerHit,
    ///                        the same per-hit cap that lets a wall survive one pickaxe swing.
    ///   damagedByExplosion   matches what DamageTile below sends, so the two halves of this suite
    ///                        are asking the game the same question.
    ///
    /// Written as one helper because the two call sites each hand-rolled the struct and both
    /// forgot the same fields.
    private void DamageEntity(Entity entity, int damage)
    {
        HealthChanges().Add(new HealthChangeBuffer
        {
            healthChange = new HealthChange
            {
                entity = entity,
                amount = -damage,
                applyToNonPredicted = true,
                bypassMaxDamagePerHit = true,
                damagedByExplosion = true,
            },
        });
    }

    private void DamageTile(int2 position, int damage, bool explosionShaped)
    {
        EntityManager.GetBuffer<TileDamageBuffer>(tileDamageBufferSingletonEntity).Add(
            new TileDamageBuffer
            {
                position = position,
                damage = damage,
                canHitLowColliders = true,
                bypassMaxDamagePerHit = explosionShaped,
                damagedByExplosion = explosionShaped,
                dontHitGroundSlime = true,
                dontPlayDamageTileEffect = true,
            });
    }
}
