using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// The other half of tile protection: the edits that never touch health.
//
// NoBreakZoneProtectionSystem guards a tile through DontDestroyOnZeroHealthCD, which covers every
// source of DAMAGE. It does not cover an edit written straight into the tilemap, and an explosion
// writes one: every bare `ground` tile inside the blast is turned into `dugUpGround` by a
// Command.Add appended to the shared TileUpdateBuffer. No entity, no health, no destroy gate — so
// nothing in the damage pipeline can refuse it, and a protected base ends up pitted.
//
// This system reads that buffer once per tick, just before the tilemap is updated from it, and
// drops the entries NoBreakZoneTileEdit refuses. The policy lives in Scripts/Logic precisely so the
// decision can be replayed exhaustively offline (Editor/LogicTests~ walks every command × tile type
// × covered × cleared combination and asserts exactly one shape is refused).
//
// WHY EndPredictedSimulationSystemGroup, OrderFirst: everything that appends to the buffer runs in
// the plain part of PredictedSimulationSystemGroup — the explosion, the equipment slots, plants,
// water — and everything that CONSUMES it (UpdateSubMapSystemServer on the server,
// SetPredictedTilePositionsSystem and UpdateSubMapClientSystem on the client) lives in this group.
// Coming first here means running after every producer and before every consumer without naming a
// single one of them.
//
// BOTH WORLDS, SAME ANSWER. Tile updates are predicted on the client, so a server-only filter would
// leave the client showing a hole the server never dug — the same class of mismatch that made
// chests into ghosts in research.md chapter 9.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
[UpdateInGroup(typeof(EndPredictedSimulationSystemGroup), OrderFirst = true)]
public partial class NoBreakZoneTileEditFilterSystem : PugSimulationSystemBase
{
    private NoBreakZonePylonRegistrySystem _registry;

    // Same shape as NoBreakZoneProtectionSystem.CachePylons: the registry hands out int2s and
    // NoBreakZoneRange takes parallel int arrays so it can stay free of Unity types.
    private int[] _pylonX = new int[8];
    private int[] _pylonZ = new int[8];
    private int _pylonCount;

    private int _refusedTotal;

    protected override void OnCreate()
    {
        NeedTileUpdateBuffer();

        // Both are managed systems on the main thread and the registry runs earlier in the frame
        // (it is ordered before PredictedSimulationSystemGroup, and this group is inside it), so
        // reading its published positions here needs no copy and no job dependency.
        _registry = World.GetOrCreateSystemManaged<NoBreakZonePylonRegistrySystem>();

        base.OnCreate();
    }

    protected override void OnUpdate()
    {
        NativeArray<int2> pylons = _registry.Positions;

        // No switched-on pylon means no square, so there is nothing this system may refuse.
        if (pylons.Length > 0)
        {
            var buffer = EntityManager.GetBuffer<TileUpdateBuffer>(tileUpdateBufferSingletonEntity);
            if (buffer.Length > 0)
            {
                CachePylons(pylons);
                Filter(buffer, NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneConfig.ProtectionDiameter));
            }
        }

        base.OnUpdate();
    }

    /// Compacts in place, keeping order. Order is load-bearing — later entries override earlier ones
    /// for the same tile — so this cannot use a swap-back removal.
    private void Filter(DynamicBuffer<TileUpdateBuffer> buffer, int radius)
    {
        int write = 0;
        int refused = 0;

        for (int read = 0; read < buffer.Length; read++)
        {
            TileUpdateBuffer entry = buffer[read];
            int2 position = entry.position;

            bool covered = NoBreakZoneRange.AllTilesCovered(
                _pylonX, _pylonZ, _pylonCount, position.x, position.y, position.x, position.y, radius);

            if (NoBreakZoneTileEdit.RefuseEdit(
                    (int)entry.command,
                    (int)entry.tile.tileType,
                    covered,
                    ClearedEarlier(buffer, read, position)))
            {
                refused++;
                continue;
            }

            buffer[write++] = entry;
        }

        if (refused == 0)
        {
            return;
        }

        buffer.RemoveRange(write, buffer.Length - write);

        // One line per burst of refusals rather than per entry: an explosion produces dozens at
        // once and this runs every tick.
        _refusedTotal += refused;
        Debug.Log($"[NoBreakZone] refused {refused} tile edit(s), {_refusedTotal} total "
                  + $"(world={World.Name})");
    }

    /// Does an earlier entry Clear this tile? EnsureSameGroundTileBeneathEntitySystem levels the
    /// ground under a newly placed object with a Clear followed by an Add at the same position, and
    /// dropping only the Add would empty the tile outright.
    ///
    /// Quadratic in the number of entries, which is fine: the buffer holds one tick's worth of edits
    /// — a single explosion contributes at most the tiles inside its radius — and it is emptied
    /// every tick by the submap update.
    private static bool ClearedEarlier(DynamicBuffer<TileUpdateBuffer> buffer, int index, int2 position)
    {
        for (int i = 0; i < index; i++)
        {
            if (buffer[i].command == TileUpdateBuffer.Command.Clear
                && math.all(buffer[i].position == position))
            {
                return true;
            }
        }

        return false;
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
}
