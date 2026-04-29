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
    // powered consumer can't drain it instantly when supply collapses. Sized so a
    // single flywheel can cover up to 3 simultaneous nominal-demand consumers (1.0
    // each) during a wind stall.
    public const float MaxRate = 3f;

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

    // Rotation pivot for the wheel child GameObject — edge-aligned tile-local from the
    // anchor's bottom-left CORNER. Centred on a 2×2 footprint, so (1.0, 1.0) sits exactly
    // at the centre of the building.
    public const float WheelHubX = 1.0f;
    public const float WheelHubY = 1.0f;

    // Degrees per second the wheel sweeps when fully charged. Linear interpolation:
    // actual speed = (charge / Capacity) × WheelDegPerSecAtMaxCharge.
    public const float WheelDegPerSecAtMaxCharge = 360f;

    public override void AttachAnimations() {
        Sprite wheelSprite = Resources.Load<Sprite>("Sprites/Buildings/flywheel_wheel");
        if (wheelSprite != null) {
            GameObject wheelGO = new GameObject("wheel");
            wheelGO.transform.SetParent(go.transform, true);
            // Edge-aligned hub; tiles centred at integer coords means anchor's bottom-left
            // CORNER sits at world (x-0.5, y-0.5).
            float hubX = mirrored ? (structType.nx - WheelHubX) : WheelHubX;
            wheelGO.transform.position = new Vector3(x - 0.5f + hubX, y - 0.5f + WheelHubY, 0f);

            SpriteRenderer wsr = wheelGO.AddComponent<SpriteRenderer>();
            wsr.sprite = wheelSprite;
            wsr.flipX = mirrored;
            // Render BEHIND the housing — the flywheel sprite is a frame around the wheel,
            // and the spokes peek through the open centre of the frame.
            wsr.sortingOrder = (sr != null ? sr.sortingOrder : 10) - 1;
            LightReceiverUtil.SetSortBucket(wsr);

            RotatingPart rot = wheelGO.AddComponent<RotatingPart>();
            // Charge fraction is unsigned [0, 1]; the flywheel always spins one direction.
            rot.speedSource         = () => charge / Capacity;
            rot.isActive            = () => IsCurrentlyActive;
            rot.degPerSecAtMaxSpeed = WheelDegPerSecAtMaxCharge;
            rot.stallThreshold      = 0f; // IsCurrentlyActive already gates on charge > 0.01
            rot.directionSign       = -1f; // negative deg = clockwise in Unity 2D
        } else {
            Debug.LogWarning("flywheel_wheel sprite missing at Resources/Sprites/Buildings/flywheel_wheel — flywheel wheel will not render.");
        }

        AttachPortStubs(Ports);
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
