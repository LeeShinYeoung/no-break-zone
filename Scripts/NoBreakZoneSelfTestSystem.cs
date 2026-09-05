using PugTilemap;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
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
// WHAT THE HUMAN DOES: switch `selfTest` on in the mod's config, load a throwaway world, place a
// pylon and switch it on. Everything after that is automatic; the verdict is greppable:
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

    private NoBreakZonePylonRegistrySystem _registry;

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
        NativeArray<int2> pylons = _registry.Positions;
        if (pylons.Length == 0)
        {
            if (++_framesWaitingForPylon > SetupTimeoutFrames)
            {
                Debug.Log("[NBZTEST] SETUP FAIL no switched-on pylon found — place one and switch it on");
                Enabled = false;
            }

            return;
        }

        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneConfig.ProtectionDiameter);
        int2 pylon = pylons[0];

        // Two tiles out is comfortably inside any square; radius + 3 is comfortably outside one,
        // and far enough that a second pylon would have to be adjacent to reach it.
        _inside = pylon + new int2(2, 0);
        _outside = pylon + new int2(radius + 3, 0);

        var tiles = CreateTileAccessor();
        _tileset = tiles.GetTop(_inside).tileset;

        Debug.Log($"[NBZTEST] pylon at ({pylon.x},{pylon.y}) radius={radius} "
                  + $"inside=({_inside.x},{_inside.y}) outside=({_outside.x},{_outside.y})");

        Advance();
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
