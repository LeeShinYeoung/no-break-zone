using PugMod;
using UnityEngine;
using UnityEngine.Scripting;

// Root component of Prefabs/NoBreakZonePylonGraphics.prefab, and the whole of the E-key toggle
// (design.md §5). The prefab's InteractableObject calls Toggle on press.
//
// THE NETWORKING IS THE GAME'S, NOT OURS. EntityMonoBehaviour.SetVariation does three things at
// once (ck-db Pug.Other/EntityMonoBehaviour.cs:429): it records a local override so the presser
// sees the change immediately, bumps a monotonic update count, and — if the entity is a ghost —
// sends the game's own SetVariationRPC. The server's SetVariationSystem validates it (guest mode
// is rejected, an update count that is not newer is ignored) and writes ObjectDataCD, which NetCode
// replicates back to everyone. So the mod needs no RPC, no command, and no server code for this.
//
// The state survives save/load for free: ObjectDataCD is part of what the world save keeps. That
// is why design.md chose the game's native variation over a file of our own.
[Preserve]
public class NoBreakZonePylonGraphics : EntityMonoBehaviour
{
    // design.md §5: off is 0 and the initial state; on is 1.
    public const int VariationOff = 0;
    public const int VariationOn = 1;

    // -1 so the first ManagedLateUpdate after spawning always applies, whatever the saved state.
    private int _appliedVariation = -1;

    // The wave in flight, if any. Negative means idle; otherwise seconds since the toggle.
    private float _waveElapsed = -1f;
    private bool _waveExpanding;
    private int _waveRingsPlayed;
    private float _waveReach;

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
        AdvanceWave();

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

    // ---------------------------------------------------------------------------------- the wave
    //
    // "A wave spreading out when switched on, a wave drawing in when switched off" — asked for on
    // 2026-09-10 after the flash alone read as a pop with no direction to it. PlayPuff bursts at
    // one point and cannot expand, so the motion is composed: a few rings of small puffs, each ring
    // a step further out (or further in) than the last, spread over well under a second. The eye
    // joins the steps into a wave.
    //
    // design.md §7 IS THE CEILING. "Do not let the wave spread to the edge of the protected range"
    // — if the wave reached the edge, flipping the switch would show the range and the lens would
    // have no reason to exist. So the outermost ring stops at WaveReachTiles, a fifth of the
    // default 10-tile radius, and is further clamped to a fraction of whatever radius the config
    // actually sets, so a small custom range cannot be given away either. The wave says "something
    // changed here"; where "here" ends is the lens's job.

    // Outermost ring, in tiles. Two is enough to read as motion on a base and nowhere near an edge.
    private const float WaveReachTiles = 2f;

    // Ceiling on reach as a share of the protected radius, for configs that shrink the range.
    private const float WaveReachShareOfRadius = 0.3f;

    // design.md §7: "lasting under one second". The rings are dealt across this many seconds.
    private const float WaveDuration = 0.4f;
    private const int WaveRings = 4;
    private const int WavePointsPerRing = 8;

    // Per point, on a puff already known to be faint. Faint is right: the ring is the picture, and
    // eight bright bursts would be eight flashes rather than one wave.
    private const int WavePuffsPerPoint = 2;

    private void StartWave(bool expanding)
    {
        int radius = NoBreakZoneRange.RadiusFromDiameter(NoBreakZoneConfig.ProtectionDiameter);
        _waveReach = Mathf.Min(WaveReachTiles, radius * WaveReachShareOfRadius);
        _waveExpanding = expanding;
        _waveRingsPlayed = 0;
        _waveElapsed = 0f;
    }

    private void AdvanceWave()
    {
        if (_waveElapsed < 0f)
        {
            return;
        }

        _waveElapsed += Time.deltaTime;

        // Rings are dealt on a clock rather than one per frame, so the wave takes the same time at
        // any frame rate; a slow frame just deals more than one ring at once.
        int ringsDue = Mathf.Min(WaveRings,
                                 Mathf.FloorToInt(_waveElapsed / (WaveDuration / WaveRings)) + 1);
        while (_waveRingsPlayed < ringsDue)
        {
            PlayRing(_waveRingsPlayed++);
        }

        if (_waveRingsPlayed >= WaveRings)
        {
            _waveElapsed = -1f;
        }
    }

    private void PlayRing(int step)
    {
        // Expanding counts outward from the first ring; contracting deals the same rings in the
        // opposite order, which is the whole of the "reverse" design.md §7 asks for on switch-off.
        float share = _waveExpanding
            ? (step + 1) / (float)WaveRings
            : (WaveRings - step) / (float)WaveRings;
        float radius = _waveReach * share;

        // Sparks going out, smoke coming in: the same two puffs the centre flash uses, so the
        // wave reads as part of that flash rather than a second effect.
        int puff = (int)(_waveExpanding ? PuffID.AncientSparks : PuffID.SmallAncientSmoke);

        // Rotated half a step every ring so consecutive rings do not line up into eight spokes.
        float offset = step * (Mathf.PI / WavePointsPerRing);
        for (int i = 0; i < WavePointsPerRing; i++)
        {
            float angle = offset + i * (2f * Mathf.PI / WavePointsPerRing);
            // transform.position is already render space (this is an EntityMonoBehaviour), and
            // PlayPuff takes render space — the centre flash proves it — so an offset from it
            // needs no conversion.
            Vector3 point = transform.position
                            + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            API.Effects.PlayPuff(puff, point, WavePuffsPerPoint);
        }
    }

    // design.md §7's "effect at the moment of activation": a short flash, under a second, and a
    // quieter reverse when switching off. Runs on whichever client observes the change, which is
    // every client — the variation is replicated, and this method hangs off the same comparison
    // that repaints.
    //
    // design.md §7 also forbids the effect reaching the edge of the protected square: "Do not let
    // the wave spread to the edge of the protected range. Otherwise switching it off and on alone
    // would reveal the range, and the lens would lose its reason to exist." The centre flash bursts
    // at one point and cannot reach anything; the wave that now follows it can, which is why its
    // reach is capped (see StartWave).
    /// Boss cues are mixed for a boss. A pylon is switched whenever somebody rearranges a base.
    private const float ToggleVolume = 0.6f;

    private void PlayToggleFeedback(bool switchedOn)
    {
        // Named rather than numbered, for the reason ObjectID taught us: names resolve against the
        // real game assembly at build time. Chosen to match design.md §4's framing of the pylon as
        // a device like an ancient relic.
        //
        // SfxTableID, NOT SfxID, AND THE DIFFERENCE IS WHY THIS WAS SILENT UNTIL 2026-09-10.
        // The two look interchangeable and are not. SfxID is an ordinary sequential enum;
        // SfxTableID is a static class of ints built by Animator.StringToHash(name), so its values
        // are string hashes. `PlaySfx(int sfxTableID, ...)` wants the hash, and a cast SfxID is a
        // small sequential number that matches no hash at all — the call succeeded and played
        // nothing, every time, with no error to show for it. The SDK's own example
        // (Examples/SpawnStuffFromTiles.cs:79) passes SfxTableID, which is what settled it.
        //
        // A MATCHED PAIR FROM ONE SOURCE. coreBossOrbPowerUp and coreBossOrbPowerDown are an on/off
        // pair the game already treats as one device powering up and down, which is what design.md
        // §4 calls the pylon — a device like an ancient relic. Turned down because a boss cue at
        // full volume is too much for something a player flips whenever they rearrange their base.
        // AFSFXPortalAppear is the nearest alternative but has no counterpart for switching off.
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
        //
        // AncientSparks at 6 was still too faint when a human looked (2026-09-10), so this is one
        // step up rather than a leap: AncientFlashingSparks keeps the same character and reads
        // brighter. AncientEnergyBurst is the next rung if it is still not enough — but not
        // AncientEnergyRing, which an earlier pass found overpowering and which design.md §7 warns
        // against for a different reason: an effect that reaches the square's edge would give the
        // range away and make the lens pointless.
        StartWave(expanding: switchedOn);

        if (switchedOn)
        {
            API.Effects.PlayPuff((int)PuffID.AncientFlashingSparks, transform.position, 10);
            API.Audio.PlaySfx(SfxTableID.coreBossOrbPowerUp, transform.position,
                              volumeMultiplier: ToggleVolume);
            return;
        }

        // design.md §7 wants the off state to fade rather than pop, so this stays deliberately
        // smaller than its counterpart rather than mirroring it.
        API.Effects.PlayPuff((int)PuffID.SmallAncientSmoke, transform.position, 4);
        API.Audio.PlaySfx(SfxTableID.coreBossOrbPowerDown, transform.position,
                          volumeMultiplier: ToggleVolume);
    }
}
