using PugMod;
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

        // The first pass is catching up with a state that already existed — a pylon streaming into
        // view, or a world loading with it switched on. Only a real change gets the fanfare;
        // otherwise walking into a base would set every pylon in it off at once.
        bool wasCatchingUp = _appliedVariation < 0;
        _appliedVariation = variation;

        // objectInfo is looked up with the current variation, so this re-picks the sprite variant.
        UpdateGraphicsFromObjectInfo(objectInfo);

        if (!wasCatchingUp)
        {
            PlayToggleFeedback(variation == VariationOn);
        }
    }

    // 기획서 §7's "활성화 순간 이펙트": a short flash, under a second, and a quieter reverse when
    // switching off. Runs on whichever client observes the change, which is every client — the
    // variation is replicated, and this method hangs off the same comparison that repaints.
    //
    // 기획서 §7 also forbids the effect reaching the edge of the protected square: "파동이 보호
    // 범위 경계까지 퍼지게 하지 않는다. 그렇게 하면 껐다 켜는 것만으로 범위를 알 수 있어 렌즈의
    // 존재 이유가 사라진다." PlayPuff bursts particles at one point and cannot expand to a radius,
    // so this stays true by construction rather than by tuning.
    private void PlayToggleFeedback(bool switchedOn)
    {
        // Named rather than numbered, for the reason ObjectID taught us: names resolve against the
        // real game assembly at build time. Chosen to match 기획서 §4's framing of the pylon as a
        // 고대 유물 같은 장치.
        //
        // The first pass used AncientBurst at 10 particles and it read as almost nothing in game.
        // Switching a pylon on is the single most consequential thing the player does with this mod
        // — it decides whether a whole base can be broken — so it gets a ring that reads at a
        // glance, a burst inside it, and the portal sound the game uses when something ancient
        // moves you somewhere.
        // SIZE IS THE PUFF, NOT THE COUNT. PlayPuff(puffId, position, particleCount) only takes a
        // number of particles; how big the effect is belongs to the puff itself. Cutting the count
        // twice on AncientEnergyRing changed nothing visible because one ring is one ring, which is
        // why it kept reading as too much. Picking a smaller puff is the only lever there is.
        //
        // With the lit sprite finally switching, the picture is what says "this is on" and the
        // effect only has to mark the moment.
        if (switchedOn)
        {
            API.Effects.PlayPuff((int)PuffID.AncientSparks, transform.position, 6);
            API.Audio.PlaySfx((int)SfxID.AF_portal_teleport, transform.position);
            return;
        }

        // 기획서 §7 wants the off state to fade rather than pop, so this stays deliberately smaller
        // than its counterpart rather than mirroring it.
        API.Effects.PlayPuff((int)PuffID.SmallAncientSmoke, transform.position, 4);
        API.Audio.PlaySfx((int)SfxID.AF_portal_collapse, transform.position);
    }
}
