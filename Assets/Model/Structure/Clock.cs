using System;
using System.Collections.Generic;
using UnityEngine;

// 1×1 building that displays in-game time via a rotating hand.
// Consumes a small amount of mechanical power. The hand only rotates while the clock
// is on a powered network (and not broken).
//
// Implements IPowerConsumer directly (not the BuildingPowerConsumer auto-wrapper) for two
// reasons:
//   1. The auto-wrapper exposes a full perimeter; the clock only accepts axles from the
//      left, right, and below — never from the top (where the hand spins).
//   2. The auto-wrapper gates demand on HasActiveCrafter(); a clock has no crafter, so
//      it needs always-on demand whenever it isn't broken/disabled.
public class Clock : Building, PowerSystem.IPowerConsumer {
    // Small constant demand. Allocation is binary per consumer, so this is either
    // satisfied in full or the clock is unpowered — there is no half-speed clock.
    // Tiny value means a single windmill / wheel comfortably powers many clocks.
    public const float Demand = 0.1f;

    // Discrete rotation positions per full rotation. 12 steps with the 12-hour cycle
    // = one snap per in-game hour, which keeps the pixel art on clean grid boundaries.
    const int HandSteps = 12;

    public Clock(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    // Spawns a child GameObject for the hand and attaches a ClockHand rotator wired to
    // the day phase + power/repair state. Same shape as Windmill/Flywheel's RotatingPart
    // wiring — building-specific knowledge lives here, the rotator is generic.
    public override void AttachAnimations() {
        base.AttachAnimations();

        Sprite handSprite = Resources.Load<Sprite>("Sprites/Buildings/clockhand");
        if (handSprite != null) {
            GameObject handGO = new GameObject("clock_hand");
            handGO.transform.SetParent(go.transform, false);
            handGO.transform.localPosition = Vector3.zero;

            // Point filter prevents bilinear blurring on the rotated sprite.
            // Also set this in the sprite's importer settings so it persists in builds.
            handSprite.texture.filterMode = FilterMode.Point;

            SpriteRenderer hsr = SpriteMaterialUtil.AddSpriteRenderer(handGO);
            hsr.sprite = handSprite;
            // Sits above the clock body (depth-0 building, sortingOrder 10) and on par
            // with platforms (15). See SPEC-rendering.md sorting-order table.
            hsr.sortingOrder = 15;
            LightReceiverUtil.SetSortBucket(hsr);

            ClockHand hand = handGO.AddComponent<ClockHand>();
            // Two full rotations per day for a 12-hour cycle. Snap to discrete steps so
            // the pixel art stays aligned.
            hand.phaseSource = () => SunController.GetDayPhase() * 2f % 1f;
            hand.isActive    = () => !IsBroken && PowerSystem.instance != null
                                       && PowerSystem.instance.IsBuildingPowered(this);
            hand.steps       = HandSteps;
        } else {
            Debug.LogWarning("clockhand sprite missing at Resources/Sprites/Buildings/clockhand — clock hand will not render.");
        }

        AttachPortStubs(Ports);
    }

    // ── IPowerConsumer ────────────────────────────────────────────────
    public Structure Structure => this;

    public float CurrentDemand => (!disabled && !IsBroken) ? Demand : 0f;

    public IEnumerable<PowerSystem.PowerPort> Ports {
        get {
            // Three port options for routing flexibility. Mirroring flips dx via the
            // standard PowerSystem.FindAttachedNetwork rule — symmetric L/R layout means
            // mirroring is purely cosmetic for this building.
            yield return new PowerSystem.PowerPort(-1, 0, PowerSystem.Axis.Horizontal); // left
            yield return new PowerSystem.PowerPort( 1, 0, PowerSystem.Axis.Horizontal); // right
            yield return new PowerSystem.PowerPort( 0,-1, PowerSystem.Axis.Vertical);   // below
        }
    }
}

// Snap-to-phase rotator — sibling of RotatingPart, but driven by an absolute phase in
// [0, 1) rather than an angular velocity. Used by Clock for its hour hand. The hand
// freezes when isActive returns false (broken or unpowered); on resume it snaps to
// the current correct angle since rotation is derived, not accumulated.
//
// Phase 0.0 → hand points straight down  (-Z = 180°)
// Phase 0.25 → hand points left          (-Z =  90°)
// Phase 0.5  → hand points straight up   (-Z =   0°)
// Phase 0.75 → hand points right         (-Z = 270°)
public class ClockHand : MonoBehaviour {
    // Returns the current phase in [0, 1). Sampled every Update.
    public Func<float> phaseSource;

    // Optional gate. When this returns false, rotation pauses and the current angle is
    // held. Null means "always active". Used to freeze the hand when broken/unpowered.
    public Func<bool>  isActive;

    // Discrete positions per full rotation. Snapping keeps the pixel art on clean
    // pixel-grid boundaries (no sub-pixel blurriness).
    public int         steps = 12;

    void Start() {
        // Set initial rotation immediately so there's no one-frame lag before the first Update.
        Apply();
    }

    void Update() {
        if (isActive != null && !isActive()) return;
        Apply();
    }

    void Apply() {
        if (phaseSource == null) return;

        float phase = phaseSource();
        float snapped = Mathf.Round(phase * steps) / steps;

        // Unity Z-rotation is counter-clockwise; negate for clockwise clock motion.
        // Offset by 180° so phase=0 (midnight) points the hand straight down.
        transform.localEulerAngles = new Vector3(0f, 0f, 180f - snapped * 360f);
    }
}
