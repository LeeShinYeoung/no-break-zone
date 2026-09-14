using Pug.UnityExtensions;
using PugMod;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// design.md §4's remote: right-click a pylon from a distance and it switches, exactly as if you had
// walked over and pressed E.
//
// WHY IT EXISTS, per design.md §4: switch a pylon on and wall yourself around it and the game is
// stuck — the walls are protected so they cannot be mined, and the pylon cannot be reached. The
// remote is the way out from inside the game. That is its whole job, which is why it targets one
// pylon and nothing more; "operate every pylon within range at once" was considered and dropped.
//
// SERVER ONLY, AND NO RPC. ClientInput is netcode command input, so the player's cursor position
// and button state are already replicated here — the same data the client sent to drive movement.
// Being on the server, writing ObjectDataCD is enough and NetCode replicates the result. That is a
// different route from stage 4's E key, which starts on a client and has to send SetVariationRPC to
// reach this side.
//
// It also avoids what looked at first like the only way in. Reacting to item use means patching
// EquipmentSlot.UpdateEquipment, which lives inside a Bursted job — the SDK's own TeleportAfterEating
// example has to disable Burst for the whole EquipmentUpdateSystem to do it and notes "this slows
// down the game". Reading the input directly costs none of that.
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class NoBreakZoneRemoteSystem : SystemBase
{
    private EntityQuery _players;
    private EntityQuery _pylons;
    private ObjectID _remoteObjectID = ObjectID.None;

    protected override void OnCreate()
    {
        _players = GetEntityQuery(
            ComponentType.ReadOnly<ClientInput>(),
            ComponentType.ReadOnly<EquippedObjectCD>(),
            ComponentType.ReadOnly<ContainedObjectsBuffer>(),
            ComponentType.ReadOnly<LocalTransform>());

        // Tagged by NoBreakZonePylonRegistrySystem, so this query is over pylons only rather than
        // over everything with an id.
        _pylons = GetEntityQuery(
            ComponentType.ReadOnly<NoBreakZonePylonCD>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadWrite<ObjectDataCD>());
    }

    protected override void OnUpdate()
    {
        if (_pylons.IsEmpty || _players.IsEmpty)
        {
            return;
        }

        if (_remoteObjectID == ObjectID.None)
        {
            _remoteObjectID = API.Authoring.GetObjectID(NoBreakZoneObjectNames.Remote);
            if (_remoteObjectID == ObjectID.None)
            {
                return;  // object database not up yet
            }
        }

        var inputs = _players.ToComponentDataArray<ClientInput>(Allocator.Temp);
        var equipped = _players.ToComponentDataArray<EquippedObjectCD>(Allocator.Temp);
        var playerTransforms = _players.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var playerEntities = _players.ToEntityArray(Allocator.Temp);
        var em = EntityManager;

        for (int i = 0; i < playerEntities.Length; i++)
        {
            // The edge, not the held state — the game already distinguishes them, so a held button
            // toggles once rather than flickering the pylon every tick.
            if (!inputs[i].IsButtonStateSet(CommandInputButtonStateNames.SecondInteract_Pressed))
            {
                continue;
            }

            if (!IsHoldingRemote(em, playerEntities[i], equipped[i]))
            {
                continue;
            }

            float2 cursor = inputs[i].mouseOrJoystickWorldPoint;
            int2 target = new int2((int)math.round(cursor.x), (int)math.round(cursor.y));
            int2 player = playerTransforms[i].Position.RoundToInt2();

            TryToggleAt(em, target, player);
        }

        inputs.Dispose();
        equipped.Dispose();
        playerTransforms.Dispose();
        playerEntities.Dispose();
    }

    // Same read the game uses in ChangeVariationWhenPlayerHoldObjectNearbySystem: the equipped slot
    // index points into the player's own inventory buffer.
    private bool IsHoldingRemote(EntityManager em, Entity player, EquippedObjectCD equipped)
    {
        if (!em.HasBuffer<ContainedObjectsBuffer>(player))
        {
            return false;
        }

        var contents = em.GetBuffer<ContainedObjectsBuffer>(player);
        int slot = equipped.equippedSlotIndex;
        if (slot < 0 || slot >= contents.Length)
        {
            return false;
        }

        return contents[slot].objectData.objectID == _remoteObjectID;
    }

    private void TryToggleAt(EntityManager em, int2 target, int2 player)
    {
        var entities = _pylons.ToEntityArray(Allocator.Temp);
        var transforms = _pylons.ToComponentDataArray<LocalTransform>(Allocator.Temp);

        for (int i = 0; i < entities.Length; i++)
        {
            int2 tile = transforms[i].Position.RoundToInt2();
            if (!tile.Equals(target))
            {
                continue;
            }

            // design.md §4: 30 tiles from the player by default, walls and line of sight ignored —
            // the point is reaching a pylon you have sealed yourself away from.
            if (!NoBreakZoneRange.IsWithinReach(
                    player.x, player.y, tile.x, tile.y, NoBreakZoneConfig.RemoteReachTiles))
            {
                break;  // right pylon, too far — and no other pylon shares this tile
            }

            Toggle(em, entities[i]);
            break;
        }

        entities.Dispose();
        transforms.Dispose();
    }

    // design.md §4: "It is only a long-range E. The result is exactly the same as pressing E in
    // front of the pylon, and only the distance differs." So this writes the same field the E key ends
    // up writing, and everything downstream — the registry picking it up, protection being applied
    // or released, the sprite and the sound on every client — follows from that one value exactly
    // as it does for E.
    private static void Toggle(EntityManager em, Entity pylon)
    {
        var data = em.GetComponentData<ObjectDataCD>(pylon);
        data.variation = data.variation == NoBreakZonePylonGraphics.VariationOn
            ? NoBreakZonePylonGraphics.VariationOff
            : NoBreakZonePylonGraphics.VariationOn;

        // The counter is what stops a client's own optimistic guess from overwriting this a moment
        // later; SetVariationSystem only accepts an update that is strictly newer.
        data.variationUpdateCount++;
        em.SetComponentData(pylon, data);

        Debug.Log($"[NoBreakZone] remote toggled a pylon to variation {data.variation}");
    }
}
