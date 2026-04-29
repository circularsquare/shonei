using System.Collections.Generic;

// A treadmill-style power producer. A "runner" mouse comes here via the standard
// CraftTask machinery (one zero-IO recipe authored in recipesDb.json). The wheel
// produces 1.0 power into its shaft network only while a runner is *on* the wheel
// — i.e. mid-WorkObjective at this building. Crucially that's NOT the same as
// "an order has been claimed": the runner still has to walk over via GoObjective
// before they start cycling. We gate on Building.HasActiveCrafter so the wheel
// stays still and silent during the walk-in.
//
// Footprint: 2×2 (anchor bottom-left). Power port: one tile to the right of the
// anchor at mid-height — i.e. just outside the wheel's right edge. Mirroring flips
// the port to the left side automatically (PowerSystem.FindAttachedNetwork handles it).
public class MouseWheel : Building, PowerSystem.IPowerProducer {
    // Scalar power produced while a worker is on the wheel. 1.0 = "one mouse-second of power".
    // Tunable; set high enough that one wheel can power one consumer (powerBoost target = 1.0).
    public const float Output = 1.0f;

    public MouseWheel(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    public override void OnPlaced() {
        base.OnPlaced(); // registers WOM craft order
        PowerSystem.instance?.RegisterProducer(this);
    }

    public override void Destroy() {
        PowerSystem.instance?.UnregisterProducer(this);
        base.Destroy();
    }

    // True iff a runner is currently in WorkObjective at this wheel. Drives both
    // power output (via CurrentOutput) and the FrameAnimator wheel-spin visual.
    public bool IsCurrentlyActive => HasActiveCrafter();

    public override void AttachAnimations() {
        AttachFrameAnimator("wheel", () => IsCurrentlyActive, baseFps: 8f);
        AttachPortStubs(Ports);
    }

    // ── IPowerProducer ────────────────────────────────────────────────
    public Structure Structure => this;
    public float CurrentOutput => IsCurrentlyActive ? Output : 0f;

    public IEnumerable<PowerSystem.PowerPort> Ports {
        get {
            // Horizontal ports on BOTH sides at bottom row, so the player can route a shaft
            // in from either direction without having to mirror the wheel. Stubs render
            // conditionally — the unused side stays clean (see PortStubVisuals).
            yield return new PowerSystem.PowerPort(-1, 0, PowerSystem.Axis.Horizontal);
            yield return new PowerSystem.PowerPort(structType.nx, 0, PowerSystem.Axis.Horizontal);
        }
    }
}
