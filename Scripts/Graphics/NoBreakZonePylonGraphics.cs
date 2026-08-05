using UnityEngine.Scripting;

// Root component of Prefabs/NoBreakZonePylonGraphics.prefab, and the whole of the E-key toggle
// (기획서 §5). The prefab's InteractableObject calls Toggle on press.
//
// THE NETWORKING IS THE GAME'S, NOT OURS. EntityMonoBehaviour.SetVariation does three things at
// once (ck-db Pug.Other/EntityMonoBehaviour.cs:429): it records a local override so the presser
// sees the change immediately, bumps a monotonic update count, and — if the entity is a ghost —
// sends the game's own SetVariationRPC. The server's SetVariationSystem validates it (guest mode
// is rejected, an update count that is not newer is ignored) and writes ObjectDataCD, which NetCode
// replicates back to everyone. So the mod needs no RPC, no command, and no server code for this.
//
// The state survives save/load for free: ObjectDataCD is part of what the world save keeps
// (research.md 10장). That is why 기획서 chose the game's native variation over a file of our own.
[Preserve]
public class NoBreakZonePylonGraphics : EntityMonoBehaviour
{
    // 기획서 §5: off is 0 and the initial state; on is 1.
    public const int VariationOff = 0;
    public const int VariationOn = 1;

    // -1 so the first ManagedLateUpdate after spawning always applies, whatever the saved state.
    private int _appliedVariation = -1;

    /// Wired to InteractableObject.onUseActions in the prefab — this is the E key.
    public void Toggle()
    {
        SetVariation(variation == VariationOn ? VariationOff : VariationOn);
    }

    // WHY POLL INSTEAD OF REACTING TO THE TOGGLE: UpdateGraphicsFromObjectInfo only runs when the
    // graphical object spawns (EntityMonoBehaviour.cs:1128). The player who pressed E would see the
    // sprite change anyway, because `variation` returns their local override — but on every other
    // client the replicated ObjectDataCD changes and nothing asks the graphics to catch up, leaving
    // a pylon that protects a base while still looking switched off.
    //
    // Comparing one int per pylon per frame buys correctness for every client and for load-from-save
    // alike. `variation` already resolves override-vs-replicated, so this needs no knowledge of
    // which case it is in.
    public override void ManagedLateUpdate()
    {
        base.ManagedLateUpdate();

        if (_appliedVariation == variation)
        {
            return;
        }

        _appliedVariation = variation;

        // objectInfo is looked up with the current variation, so this re-picks the sprite variant.
        UpdateGraphicsFromObjectInfo(objectInfo);
    }
}
