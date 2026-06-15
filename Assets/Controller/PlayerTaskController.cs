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
    // -1 = uninitialized: the controller is created in World.Awake, but the real index isn't
    // known until ResetState (new world) or the save restore runs — both synchronous inside the
    // load. Starting at -1 keeps Current null during that boot window, so the card doesn't briefly
    // treat task 0 ("press space to unpause") as live and spuriously complete it (timeScale
    // defaults to running mid-boot, which would satisfy that probe). Settable for the restore.
    public int currentIndex { get; set; } = -1;

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

    // True when Current is the final onboarding task — the card uses this to show the
    // "all complete!" finale instead of the per-task "complete!" celebration.
    public bool OnLastTask => tasks != null && currentIndex == tasks.Count - 1;

    public void ResetState() {
        currentIndex = 0;
    }

    // ── Task list ───────────────────────────────────────────────────────
    void BuildTasks() {
        tasks = new List<PlayerTask> {
            new PlayerTask("unpause", "press space to unpause",
                () => new TaskProgress(Time.timeScale > 0f ? 1 : 0, 1)),
            new PlayerTask("flag_trees", "flag 2 trees to harvest",
                () => new TaskProgress(CountFlaggedPlants(p => YieldsWood(p.plantType)), 2)),
            new PlayerTask("flag_wheat", "flag 3 wheat to harvest",
                () => new TaskProgress(CountFlaggedPlants(p => p.plantType.name == "wheat"), 3)),
            new PlayerTask("build_crates", "build 3 storage crates\nhold shift to place multiple",
                () => new TaskProgress(CountStructures("crate"), 3)),
            new PlayerTask("configure_crates", "set 1 crate to wood, 1 to wheat",
                () => new TaskProgress(CountConfiguredCrates(), 2)),
            new PlayerTask("house_mice", "house all mice",
                () => HousingProgress()),
            new PlayerTask("build_sawmill", "build a sawmill",
                () => new TaskProgress(CountStructures("sawmill"), 1)),
            new PlayerTask("assign_woodworker", "assign a woodworker",
                () => new TaskProgress(CountJob("woodworker"), 1)),
            new PlayerTask("build_drawer", "build a drawer",
                () => new TaskProgress(CountStructures("drawer"), 1)),
            new PlayerTask("food_stockpile", "stockpile 2 days of food",
                () => FoodDaysProgress()),
            new PlayerTask("six_mice", "have 6 mice\nclick happiness for details",
                () => new TaskProgress(CountMice(), 6)),
            new PlayerTask("build_laboratory", "build a laboratory",
                () => new TaskProgress(CountStructures("laboratory"), 1)),
            new PlayerTask("assign_scientist", "assign a scientist",
                () => new TaskProgress(CountJob("scientist"), 1)),
            new PlayerTask("research_tools", "research Tools",
                () => new TaskProgress(
                    ResearchSystem.instance != null && ResearchSystem.instance.IsUnlockedByName("Tools") ? 1 : 0, 1)),
            new PlayerTask("build_workshop", "build a workshop",
                () => new TaskProgress(CountStructures("workshop"), 1)),
            // Digging pit (unlocked from the start) yields stone as a rare drop; the quarry
            // is locked behind Mining research, so the practical tutorial path is the pit.
            // Count both in case the player went the Mining route.
            new PlayerTask("build_digsite", "build a digging pit",
                () => new TaskProgress(CountStructures("digging pit") + CountStructures("quarry"), 1)),
            new PlayerTask("gather_stone", "gather 3 stone",
                () => new TaskProgress(StoneLiang(), 3)),
            new PlayerTask("craft_tools", "craft stone tools",
                () => new TaskProgress(StoneToolsCount(), 1)),
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

    // A plant is a "tree" for the harvest task if it yields wood (or any wood child like
    // pine/oak) on harvest — a semantic test that includes pine/oak trees but excludes the
    // appletree (yields apples) and bamboo (own item), without hardcoding species names.
    // Uses the group-wildcard helper so "wood" matches any descendant leaf.
    static bool YieldsWood(PlantType pt) {
        if (pt?.products == null) return false;
        if (!Db.itemByName.TryGetValue("wood", out Item wood)) return false;
        foreach (ItemQuantity p in pt.products)
            if (p?.item != null && Inventory.MatchesItem(p.item, wood)) return true;
        return false;
    }

    int CountStructures(string structName) {
        if (StructController.instance == null) return 0;
        if (!Db.structTypeByName.TryGetValue(structName, out StructType st)) return 0;
        // GetByType returns null (not empty) when no instances exist yet; counting a
        // not-yet-built structure would NRE every frame and freeze the card on the
        // prior task's text. Guard like WorkOrderManager.cs:813.
        List<Structure> list = StructController.instance.GetByType(st);
        return list != null ? list.Count : 0;
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
        if (crates == null) return 0; // null (not empty) when no crates exist yet — same footgun as CountStructures
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

    // Number of mice currently assigned the given job (via the jobCounts tally,
    // keyed by the canonical Job from Db.jobByName).
    int CountJob(string jobName) {
        AnimalController ac = AnimalController.instance;
        if (ac?.jobCounts == null) return 0;
        if (!Db.jobByName.TryGetValue(jobName, out Job j) || j == null) return 0;
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

    // Days of colony food in storage, per current mouse — reuses AnimalController's calc so
    // the onboarding bar matches the happiness/food UI. Infinity (no mice yet) counts as
    // satisfied so the step never wedges before any mice exist.
    TaskProgress FoodDaysProgress() {
        AnimalController ac = AnimalController.instance;
        if (ac == null) return new TaskProgress(0, 2);
        float days = ac.ComputeDaysOfFoodInStorage();
        if (float.IsInfinity(days)) return new TaskProgress(2, 2);
        return new TaskProgress((int)days, 2);
    }

    // Stone on hand in liang (GlobalInventory stores fen; 100 fen = 1 liang). Group-summed
    // across all stone leaves (slate/granite/...).
    int StoneLiang() {
        GlobalInventory ginv = GlobalInventory.instance;
        return ginv != null ? ginv.Quantity("stone") / 100 : 0;
    }

    // 1 once any stone tools exist. Discrete items only ever hold whole units, so any
    // positive fen quantity means at least one crafted tool.
    int StoneToolsCount() {
        GlobalInventory ginv = GlobalInventory.instance;
        return (ginv != null && ginv.Quantity("stone tools") > 0) ? 1 : 0;
    }
}
