using System.Collections.Generic;
using UnityEngine;

// Mechanical-power storage. Charges from any network surplus (after live consumers are
// served), discharges to fill any deficit, and exponentially bleeds energy each tick to
// reflect friction / air drag — even on an idle network.
//
// Footprint: 2×2 (anchor bottom-left). Perimeter ports so a shaft can attach from any side.
//
// Tunables (constants below) determine feel:
//   - Capacity:    how much energy the wheel holds at full spin.
//   - MaxRate:     how quickly it can charge or discharge (per-tick cap, both directions).
//   - DecayFactor: per-tick exponential bleed; half-life = ln(0.5) / ln(DecayFactor) ticks.
//
// Save/load: charge persists via StructureSaveData.flywheelCharge. Without it, every
// flywheel would reset to empty on load and the player would lose stored power.
public class Flywheel : Building, PowerSystem.IPowerStorage {
    // Maximum stored energy in power-units. With Wheel output = 1 and per-tick allocation,
    // a fully charged flywheel can supply 1.0 power for ~Capacity ticks against decay.
    public const float Capacity = 50f;

    // Per-tick cap on charge or discharge magnitude. Limits how fast a flywheel can
    // smooth out bursts — a tiny windmill can't fully charge it in one tick, and a
    // powered consumer can't drain it instantly when supply collapses.
    public const float MaxRate = 2f;

    // Exponential decay applied each tick. 0.97 ≈ 23-tick half-life ≈ ~2.3 in-game hours
    // (240 ticks/day, 10 ticks/hour). Tuned so a flywheel charged from windmill gusts can
    // carry through a calm period of similar length but isn't a long-term battery.
    public const float DecayFactor = 0.97f;

    // Current stored energy in [0, Capacity]. Persisted via SaveSystem.GatherStructure.
    public float charge;

    public Flywheel(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    public override void OnPlaced() {
        base.OnPlaced();
        PowerSystem.instance?.RegisterStorage(this);
    }

    public override void Destroy() {
        PowerSystem.instance?.UnregisterStorage(this);
        base.Destroy();
    }

    // ── IPowerStorage ────────────────────────────────────────────────
    public Structure Structure => this;

    public float MaxDischarge => IsBroken ? 0f : Mathf.Min(charge, MaxRate);
    public float MaxIntake    => IsBroken ? 0f : Mathf.Min(Capacity - charge, MaxRate);

    public void ApplyDelta(float delta) {
        charge = Mathf.Clamp(charge + delta, 0f, Capacity);
    }

    public void StorageTick() {
        // Tiny floor to avoid float dust hanging around forever — InfoPanel "supply"
        // wouldn't show 0 from a flywheel that decays asymptotically.
        if (charge < 1e-3f) charge = 0f;
        else charge *= DecayFactor;
    }

    // Visible spin gates on any non-trivial charge; speed scales with charge fraction
    // so a flywheel visibly winds down as it bleeds energy.
    public bool IsCurrentlyActive => charge > 0.01f;

    public override void AttachAnimations() {
        AttachFrameAnimator("flywheel",
            () => IsCurrentlyActive,
            baseFps: 10f,
            speedMul: () => charge / Capacity);
    }

    public IEnumerable<PowerSystem.PowerPort> Ports {
        get {
            // Perimeter ports: same convention as BuildingPowerConsumer. Lets the player
            // attach shafts to any side of the flywheel for routing flexibility.
            int nx = structType.nx;
            int ny = Mathf.Max(1, structType.ny);
            for (int i = 0; i < nx; i++) {
                yield return new PowerSystem.PowerPort(i, -1, PowerSystem.Axis.Both);
                yield return new PowerSystem.PowerPort(i, ny, PowerSystem.Axis.Both);
            }
            for (int j = 0; j < ny; j++) {
                yield return new PowerSystem.PowerPort(-1, j, PowerSystem.Axis.Both);
                yield return new PowerSystem.PowerPort(nx, j, PowerSystem.Axis.Both);
            }
        }
    }
}
