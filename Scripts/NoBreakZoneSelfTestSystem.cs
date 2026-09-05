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
// WHAT ANYONE HAS TO DO: nothing, when it runs on the dedicated server
// (D:\NoBreakZoneServer\run-selftest.ps1) — it builds its own switched-on pylon and reports. In a
// real world a human can instead place a pylon within the first ten seconds and it will use that
// one. Either way the verdict is greppable out of the log:
//
//     [NBZTEST] floor-inside-explosion PASS
//     [NBZTEST] SUMMARY pass=6 fail=0 skip=0
//
// OFF BY DEFAULT, AND IT HAS TO STAY THAT WAY. It damages tiles on purpose — including outside the
// square, where they are supposed to break — so it must never run in somebody's real base.
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
    private const int SetupTimeoutFrames = 3600;

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

    private int _step;
    private int _wait;
    private int _framesWaitingForPylon;
    private int _pass;
    private int _fail;
    private int _skip;

    private int2 _inside;
    private int2 _outside;
    private int _tileset;

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
        if (!NoBreakZoneConfig.SelfTest)
        {
            Enabled = false;
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
            default: Finish(); break;
        }

        base.OnUpdate();
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
            if (_framesWaitingForPylon >= FramesBeforeSelfProvisioning && _framesWaitingForPylon % 60 == 0)
            {
                ProbeAndBuildPylon();
            }
            else if (_framesWaitingForPylon > SetupTimeoutFrames)
            {
                Debug.Log("[NBZTEST] SETUP FAIL no switched-on pylon, and building one did not take");
                Enabled = false;
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
            || !TryFindGround(tiles, pylon, radius + 2, radius + 14, out _outside))
        {
            Debug.Log("[NBZTEST] SETUP FAIL could not find bare ground both inside and outside "
                      + $"the square around ({pylon.x},{pylon.y})");
            Enabled = false;
            return;
        }

        _tileset = tiles.GetTop(_inside).tileset;

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
        if (!TryFindGround(tiles, int2.zero, 0, SearchRadius, out int2 at))
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

        // variation 1 is the switched-on look, and the registry reads exactly that field to decide
        // whether a pylon projects a square (NoBreakZonePylonGraphics.VariationOn).
        Entity pylon = EntityUtility.CreateEntity(
            World, new Vector3(at.x, 0f, at.y), pylonId, 1, database,
            NoBreakZonePylonGraphics.VariationOn);

        Debug.Log(pylon == Entity.Null
            ? "[NBZTEST] SETUP FAIL could not create a pylon entity"
            : $"[NBZTEST] built a switched-on pylon at ({at.x},{at.y})");
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
            _step = 4;
            _wait = FramesBetweenSteps;
            return;
        }

        DamageTile(_inside, ExplosionDamage, explosionShaped: true);
        DamageTile(_outside, ExplosionDamage, explosionShaped: true);
        Advance();
    }

    private void CheckExplosionResult()
    {
        var tiles = CreateTileAccessor();

        // THE REPORTED BUG.
        Verdict("floor-inside-explosion",
            tiles.GetTopType(_inside) == TileType.floor,
            "the floor inside the square survives an explosion");

        // The control. Without it the check above passes just as well when nothing works at all —
        // for instance if the damage never reached the tile.
        Verdict("floor-outside-explosion",
            tiles.GetTopType(_outside) != TileType.floor,
            "the floor outside every square still breaks");

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

    private void Finish()
    {
        Debug.Log($"[NBZTEST] SUMMARY pass={_pass} fail={_fail} skip={_skip}");
        Enabled = false;
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
                    if (tiles.GetTopType(candidate) == TileType.ground)
                    {
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
        _step++;
        _wait = FramesBetweenSteps;
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
