using System;
using System.Collections.Generic;
using UnityEngine;

// PlayerTask — one onboarding goal surfaced to the player, shown one at a time on
// the Tasks card. Deliberately NOT the same thing as the mouse-AI Task/Objective
// system: those are work dispatched to animals; a PlayerTask is player-facing
// guidance with no effect on the simulation.
//
// A task is just a title plus a Progress() probe returning (current, target).
// IsComplete is current >= target. See plans/playertasks.md.
public class PlayerTask {
    public readonly string id;       // stable id, used by the save system
    public readonly string title;    // player-facing card text — concise, ASCII only
    readonly Func<TaskProgress> probe;

    public PlayerTask(string id, string title, Func<TaskProgress> probe) {
        this.id    = id;
        this.title = title;
        this.probe = probe;
    }

    public TaskProgress Progress() => probe();
    public bool IsComplete => probe().Complete;
}

// Small value type so probes can report partial progress ("2/3") without tuples.
public struct TaskProgress {
    public int current;
    public int target;
    public TaskProgress(int current, int target) { this.current = current; this.target = target; }
    public bool Complete => current >= target;
}

// PlayerTaskController — owns the ordered onboarding task list, advances through it,
// and (later) drives the Tasks card UI. Polls only the *current* task's probe on a
// slow tick, so cost is one predicate per tick regardless of list length. No events
// are needed: none of the underlying systems fire change callbacks for these states,
// and one-at-a-time evaluation makes polling cheap.
//
// Bootstrapped from code (EnsureExists) rather than placed in the scene, so adding
// onboarding doesn't require a .unity edit.
public class PlayerTaskController : MonoBehaviour {
    public static PlayerTaskController instance { get; private set; }

    List<PlayerTask> tasks;
    public int currentIndex { get; set; } // settable so the save system can restore progress

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        BuildTasks();
    }

    void OnDestroy() {
        if (instance == this) instance = null;
    }

    // Create the controller GameObject if it doesn't exist yet. Called from World.Awake.
    public static void EnsureExists() {
        if (instance != null) return;
        new GameObject("PlayerTaskController").AddComponent<PlayerTaskController>();
    }

    public PlayerTask Current =>
        (tasks != null && currentIndex >= 0 && currentIndex < tasks.Count) ? tasks[currentIndex] : null;

    public void ResetState() {
        currentIndex = 0;
    }

    // ── Task list ───────────────────────────────────────────────────────
    void BuildTasks() {
        tasks = new List<PlayerTask> {
            new PlayerTask("flag_trees", "Flag 3 trees to harvest",
                () => new TaskProgress(CountFlaggedPlants(p => p.plantType.name.Contains("tree")), 3)),
            new PlayerTask("flag_wheat", "Flag 3 wheat to harvest",
                () => new TaskProgress(CountFlaggedPlants(p => p.plantType.name == "wheat"), 3)),
            new PlayerTask("build_crates", "Build 3 crates",
                () => new TaskProgress(CountStructures("crate"), 3)),
            new PlayerTask("configure_crates", "Set 1 crate to wood, 1 to wheat",
                () => new TaskProgress(CountConfiguredCrates(), 2)),
            new PlayerTask("house_mice", "House all mice",
                () => HousingProgress()),
            new PlayerTask("build_sawmill", "Build a sawmill",
                () => new TaskProgress(CountStructures("sawmill"), 1)),
            new PlayerTask("assign_woodworker", "Assign a woodworker",
                () => new TaskProgress(CountWoodworkers(), 1)),
            new PlayerTask("build_drawer", "Build a drawer",
                () => new TaskProgress(CountStructures("drawer"), 1)),
            new PlayerTask("six_mice", "Have 6 mice",
                () => new TaskProgress(CountMice(), 6)),
            new PlayerTask("build_laboratory", "Build a laboratory",
                () => new TaskProgress(CountStructures("laboratory"), 1)),
        };
    }

    // ── Advance ──────────────────────────────────────────────────────────
    // Called by PlayerTaskCard once the current task is complete and its
    // "complete!" celebration has elapsed. Detection + timing live in the card
    // (which runs on unscaled time, so onboarding progresses even while the game
    // is paused after worldgen); the controller just steps the index.
    public void Advance() {
        if (tasks == null || currentIndex >= tasks.Count) return;
        Debug.Log($"[PlayerTask] completed: {tasks[currentIndex].id} ({tasks[currentIndex].title})");
        currentIndex++;
        if (currentIndex >= tasks.Count) Debug.Log("[PlayerTask] all onboarding tasks complete");
    }

    // ── Probes ──────────────────────────────────────────────────────────
    int CountFlaggedPlants(Func<Plant, bool> match) {
        if (PlantController.instance == null) return 0;
        int n = 0;
        foreach (Plant p in PlantController.instance.Plants)
            if (p.harvestFlagged && match(p)) n++;
        return n;
    }

    int CountStructures(string structName) {
        if (StructController.instance == null) return 0;
        if (!Db.structTypeByName.TryGetValue(structName, out StructType st)) return 0;
        return StructController.instance.GetByType(st).Count;
    }

    // Requires two DISTINCT crates: one filtered for wood, a separate one for wheat.
    // Returns 0/1/2 (2 = complete). 1 means at least one of the two is configured but
    // not yet a distinct wood+wheat pair.
    int CountConfiguredCrates() {
        if (StructController.instance == null) return 0;
        if (!Db.structTypeByName.TryGetValue("crate", out StructType crateType)) return 0;
        if (!Db.itemByName.TryGetValue("wood", out Item wood)) return 0;
        if (!Db.itemByName.TryGetValue("wheat", out Item wheat)) return 0;

        List<Structure> crates = StructController.instance.GetByType(crateType);
        bool anyConfigured = false;
        foreach (Structure ws in crates) {
            if (!CrateAllows(ws, wood)) continue;
            anyConfigured = true;
            foreach (Structure hs in crates)
                if (hs != ws && CrateAllows(hs, wheat)) return 2; // distinct wood + wheat crates
        }
        if (!anyConfigured)
            foreach (Structure hs in crates)
                if (CrateAllows(hs, wheat)) { anyConfigured = true; break; }
        return anyConfigured ? 1 : 0;
    }

    static bool CrateAllows(Structure s, Item item) {
        return s is Building b && b.storage?.allowed != null
            && b.storage.allowed.TryGetValue(item.id, out bool v) && v;
    }

    // Current live population (dead mice leave null slots, so count non-null up to na).
    int CountMice() {
        AnimalController ac = AnimalController.instance;
        if (ac == null || ac.animals == null) return 0;
        int n = 0;
        for (int i = 0; i < ac.na; i++)
            if (ac.animals[i] != null) n++;
        return n;
    }

    // Number of mice currently assigned the woodworker job (via the jobCounts tally,
    // keyed by the canonical Job from Db.jobByName).
    int CountWoodworkers() {
        AnimalController ac = AnimalController.instance;
        if (ac?.jobCounts == null) return 0;
        if (!Db.jobByName.TryGetValue("woodworker", out Job j) || j == null) return 0;
        return ac.jobCounts.TryGetValue(j, out int cnt) ? Mathf.Max(0, cnt) : 0;
    }

    TaskProgress HousingProgress() {
        AnimalController ac = AnimalController.instance;
        if (ac == null || ac.animals == null) return new TaskProgress(0, 1);
        int total = 0, housed = 0;
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a == null) continue;
            total++;
            // Mirror Animal's own "needs home" check (Animal.cs ~1043), inverted.
            if (a.homeBuilding != null && a.homeBuilding.structType.isHousing && !a.homeBuilding.IsBroken)
                housed++;
        }
        if (total == 0) return new TaskProgress(0, 1); // no mice yet — not complete
        return new TaskProgress(housed, total);
    }
}
