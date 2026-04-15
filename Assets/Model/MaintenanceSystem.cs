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
// Decay rate is calibrated so a pristine structure reaches BreakThreshold (0.5)
// in roughly DaysToBreak (30) in-game days, and fully bottoms out in ~60 days.
//   step = (1 - BreakThreshold) / (DaysToBreak * ticksInDay)
//        = 0.5 / (30 * 240) ≈ 6.94e-5 per tick
//
// Two state tracks on the system, not the structure:
//   registered  — structures currently holding a WOM Maintenance order (stops double-registers)
//   broken      — structures currently below BreakThreshold (so we fire OnBroken/OnRepaired edges only)
// Structure destruction clears both via ForgetStructure().
public class MaintenanceSystem {
    public static MaintenanceSystem instance { get; private set; }

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
        float step = (1f - Structure.BreakThreshold) / (Structure.DaysToBreak * World.ticksInDay);

        List<Structure> structures = StructController.instance?.GetStructures();
        if (structures == null) return;

        foreach (Structure s in structures) {
            if (s == null || !s.NeedsMaintenance) continue;

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
    }

    void OnUnbroken(Structure s) {
        s.RefreshTint();
    }
}
