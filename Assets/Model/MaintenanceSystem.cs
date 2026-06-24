using System.Collections.Generic;
using UnityEngine;

// Drives condition decay and WOM Maintenance order registration for all structures.
//
// Lifecycle:
//   Create()  — called from World.Awake() (mirrors WeatherSystem).
//   Tick()    — called from World.Update() on the 1-second cadence.
//
// Per tick: decrement every maintained structure's condition by one "decay step",
// cross-check thresholds, fire callbacks, and register WOM Maintenance orders
// when a structure first slips below RegisterThreshold.
//
// Decay rate is calibrated so a SHELTERED (covered) structure reaches BreakThreshold (0.5)
// in roughly DaysToBreak (30) in-game days.
//   baseStep = (1 - BreakThreshold) / (DaysToBreak * ticksInDay)
//            = 0.5 / (30 * 240) ≈ 6.94e-5 per tick   (the sheltered rate)
//
// Structures open to the sky (no roof / overhead cover) decay 1.5× faster —
// Structure.ExposedDecayFactor, applied per-structure in Tick via IsSheltered. The roof
// incentive: a covered structure lasts 1.5× as long as an exposed one.
//
// Two state tracks on the system, not the structure:
//   registered  — structures currently holding a WOM Maintenance order (stops double-registers)
//   broken      — structures currently below BreakThreshold (so we fire OnBroken/OnRepaired edges only)
// Structure destruction clears both via ForgetStructure().
//
// ── Broken-state gating pattern ──────────────────────────────────────
// Two tiers:
//   1. Edge callbacks (OnBroken / OnUnbroken): fire once on threshold crossing.
//      Used only for visual tint swap (RefreshTint). Keep minimal.
//   2. Polling (Structure.IsBroken at use sites): each system checks at its own
//      natural cadence. No central dispatch needed — the property is cheap (two
//      comparisons). Current poll sites:
//        - LightSource.UpdateLitState()           — suppresses fuel burn + emission
//        - WaterController.UpdateSurfaceMask()     — hides fountain decorative water
//        - Clock.AttachAnimations (ClockHand.isActive closure) — freezes clock hand rotation
//        - Animal.ScanForNearbyDecorations()       — skips decoration happiness
//        - Animal.FindHome() / TryPickLeisure()    — skips houses / leisure seats
//        - WOM isActive lambdas (craft/research/fuel) — suppresses work orders
//        - Structure.EffectivePathCostReduction    — zeroes road speed bonus
// When adding a new broken-state effect, prefer polling IsBroken at the site
// that already decides the behaviour, rather than adding to OnBroken/OnUnbroken.
public class MaintenanceSystem {
    public static MaintenanceSystem instance { get; private set; }

    // Reload-Domain-off support: plain C# classes don't get their static `instance`
    // nulled by Unity when entering play mode if domain reload is disabled. Without
    // this, the second play press finds the previous session's instance still here
    // and the ctor's duplicate-detection LogError fires.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

    // Structures that currently have an active WOM Maintenance order registered.
    // We only register when condition first drops below RegisterThreshold, so this
    // prevents re-registering on every tick below the threshold.
    readonly HashSet<Structure> registered = new HashSet<Structure>();

    // Structures currently in the broken (< BreakThreshold) state. Used to detect
    // downward / upward threshold crossings so OnBroken / OnRepaired fires exactly once
    // per crossing instead of every tick.
    readonly HashSet<Structure> broken = new HashSet<Structure>();

    public static MaintenanceSystem Create() {
        instance = new MaintenanceSystem();
        return instance;
    }

    // Called from World.Update() every 1 in-game second.
    public void Tick() {
        float baseStep = (1f - Structure.BreakThreshold) / (Structure.DaysToBreak * World.ticksInDay);

        List<Structure> structures = StructController.instance?.GetStructures();
        if (structures == null) return;

        foreach (Structure s in structures) {
            if (s == null || !s.NeedsMaintenance) continue;

            // baseStep is the SHELTERED rate (a covered structure breaks in DaysToBreak days).
            // Open to the sky weathers 1.5× faster — the incentive to roof things over.
            float step = IsSheltered(s) ? baseStep : baseStep * Structure.ExposedDecayFactor;

            float before = s.condition;
            float after = Mathf.Max(0f, before - step);
            s.condition = after;

            // Register WOM order the first tick we dip below RegisterThreshold.
            if (after < Structure.RegisterThreshold && !registered.Contains(s)) {
                registered.Add(s);
                WorkOrderManager.instance?.RegisterMaintenance(s);
            }

            // Downward crossing of BreakThreshold → apply broken effects.
            if (after < Structure.BreakThreshold && !broken.Contains(s)) {
                broken.Add(s);
                OnBroken(s);
            }
        }
    }

    // Called after a mender task bumps a structure's condition. Handles the upward
    // crossings of BreakThreshold (un-break) and RegisterThreshold (WOM order goes idle).
    // Called directly by MaintenanceTask on complete — not polled.
    public void OnRepaired(Structure s) {
        if (s == null) return;

        if (s.condition >= Structure.BreakThreshold && broken.Contains(s)) {
            broken.Remove(s);
            OnUnbroken(s);
        }

        // Note: we do NOT remove from `registered` on repair. The WOM order's
        // isActive lambda (s.WantsMaintenance) suppresses it when condition ≥ 0.75,
        // and removing would force a re-register on the next decay tick — churn.
        // The order is only dropped when the structure is destroyed (ForgetStructure).
    }

    // Called from Structure.Destroy() so we stop trying to track a dead reference.
    public void ForgetStructure(Structure s) {
        registered.Remove(s);
        broken.Remove(s);
    }

    // Save/load: load path restores condition onto structures first, then calls this
    // to rebuild our bookkeeping (which structures are below thresholds). Avoids
    // firing OnBroken side-effects at load — we set isLit / disabled state purely
    // via the persisted condition value and let the normal WOM Reconcile pass handle
    // order registration.
    public void RebuildFromWorld() {
        registered.Clear();
        broken.Clear();
        List<Structure> structures = StructController.instance?.GetStructures();
        if (structures == null) return;
        foreach (Structure s in structures) {
            if (s == null || !s.NeedsMaintenance) continue;
            if (s.condition < Structure.RegisterThreshold) registered.Add(s);
            if (s.condition < Structure.BreakThreshold)    broken.Add(s);
        }
    }

    // Fired on downward crossing of BreakThreshold. Keep this minimal — most gating
    // happens via IsBroken checks in WOM isActive lambdas / Animal decoration scans /
    // ModifierSystem / Navigation / LightSource.Update (they poll per frame or per task).
    // This is the place to refresh visual tint.
    void OnBroken(Structure s) {
        s.RefreshTint();
        OnBrokenStateChanged(s);
    }

    void OnUnbroken(Structure s) {
        s.RefreshTint();
        OnBrokenStateChanged(s);
    }

    // Side effects that need to fire on EITHER threshold crossing. Power shafts change the
    // network's conductivity when they break/repair (a broken shaft severs the run — see
    // PowerSystem.RebuildTopology), so the topology must rebuild. Producers/consumers/storage
    // self-gate their output to 0 when broken and need no rebuild.
    void OnBrokenStateChanged(Structure s) {
        if (s is PowerShaft) PowerSystem.instance?.MarkDirty();
    }

    // A structure is sheltered when its entire top row is covered from the sky — a roof,
    // platform, or any solidTop/blocksRain structure overhead. Mirrors Windmill.HasOpenSky:
    // both read World.IsExposedAbove, the per-tile blocker the whole sim already shares
    // (windmill output, snow, soil moisture, item wet-decay).
    static bool IsSheltered(Structure s) {
        World world = World.instance;
        if (world == null) return false;
        int topY = s.y + s.Shape.ny - 1;
        for (int dx = 0; dx < s.Shape.nx; dx++)
            if (world.IsExposedAbove(s.x + dx, topY)) return false;
        return true;
    }
}
