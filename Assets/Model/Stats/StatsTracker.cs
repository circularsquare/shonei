using System.Collections.Generic;
using UnityEngine;

// Colony-wide historic statistics — a pure C# singleton created from World.Awake()
// alongside WeatherSystem / MoistureSystem. Owns a set of DailyStat series, each a
// per-day historic value (food points produced, average social satisfaction, …).
//
// Adding a new tracked metric is one line in RegisterDefaults():
//   • Event/total metric (push) — register a Sum stat, then feed it from the
//     relevant game code: StatsTracker.instance?.Record("id", value). Food
//     production is fed generically via NoteProduced (the production firehose).
//   • Sampled/average metric (pull) — register an Average stat with a sampler
//     lambda. It samples itself on the hourly cadence; no other wiring needed.
//
// Cadence is driven by World.Tick:
//   • OnSampleTick()  — once per in-game hour: pulls every sampler stat.
//   • OnDayElapsed()  — at each day boundary: finalizes every stat's day and
//                       starts a fresh accumulator.
//
// Persistence: stats serialize to WorldSaveData.stats keyed by id (see SaveSystem
// GatherStats / RestoreStats). Unknown ids on load are ignored and registered
// stats with no saved data stay empty, so adding/removing a stat is save-safe.
public class StatsTracker {
    public static StatsTracker instance { get; private set; }

    // Reload-Domain-off support — mirrors MoistureSystem.ResetStatics. World.OnDestroy
    // also calls this on teardown so a stale instance never survives into the next world.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

    // Days of history retained per stat. Capacity is intentionally larger than any
    // current display window (charts pick their own span via GetSeries) so widening
    // a chart later doesn't need a save migration.
    public const int HistoryCapacity = 64;

    readonly List<DailyStat> stats   = new List<DailyStat>();
    readonly Dictionary<string, DailyStat> byId = new Dictionary<string, DailyStat>();

    public static StatsTracker Create() {
        instance = new StatsTracker();
        return instance;
    }

    StatsTracker() {
        RegisterDefaults();
    }

    void RegisterDefaults() {
        // Food points produced per day. Push-fed from NoteProduced: every harvest /
        // craft / construction output routes through Animal.Produce → NoteProduced,
        // which converts edible output to food points and records it here.
        Add(new DailyStat("food_produced", "Food/day", DailyStat.Agg.Sum, HistoryCapacity));

        // Food points consumed per day (mice eating). Push-fed from Animal.HandleNeeds via
        // NoteConsumed. Charted downward opposite food_produced for an at-a-glance balance.
        Add(new DailyStat("food_consumed", "Food eaten/day", DailyStat.Agg.Sum, HistoryCapacity));

        // Colony-average social satisfaction, sampled hourly. Not charted yet, but
        // registered now so history accrues from day one — and as a worked example
        // of the pull/sampler pattern for future graphs.
        Add(new DailyStat("avg_social", "Social/day", DailyStat.Agg.Average, HistoryCapacity,
            sampler: SampleAvgSocial));
    }

    // Colony mean of the "social" satisfaction need. Null when there are no animals
    // to average (skips the sample rather than recording a misleading 0).
    static float? SampleAvgSocial() {
        var ac = AnimalController.instance;
        if (ac == null || ac.na == 0) return null;
        float sum = 0f;
        for (int i = 0; i < ac.na; i++)
            sum += ac.animals[i].happiness.GetSatisfaction("social");
        return sum / ac.na;
    }

    void Add(DailyStat s) {
        if (byId.ContainsKey(s.id)) { Debug.LogError($"StatsTracker: duplicate stat id '{s.id}'"); return; }
        stats.Add(s);
        byId[s.id] = s;
    }

    public DailyStat Get(string id) => byId.TryGetValue(id, out var s) ? s : null;
    public IReadOnlyList<DailyStat> All => stats;

    // ── Recording ─────────────────────────────────────────────────────────────

    // Generic production firehose — call from the production chokepoints (Animal.Produce,
    // Processor output). Extracts whatever per-item stats are tracked; cheap when an item
    // matches none. `fen` is the produced quantity in fen (100 fen = 1 liang).
    public void NoteProduced(Item item, int fen) {
        if (item == null || fen <= 0) return;
        // Food points = (liang produced) × foodValue, matching the nutrition math used
        // in AnimalController.totalHunger and Task fetch sizing.
        if (item.foodValue > 0f)
            Get("food_produced")?.Record(fen / 100f * item.foodValue);
    }

    // Consumption firehose — mirrors NoteProduced. Call when food is eaten; `fen` is the
    // quantity consumed. Food points = (liang eaten) × foodValue, the nutrition restored.
    public void NoteConsumed(Item item, int fen) {
        if (item == null || fen <= 0) return;
        if (item.foodValue > 0f)
            Get("food_consumed")?.Record(fen / 100f * item.foodValue);
    }

    // Direct push for non-item stats.
    public void Record(string id, float value) => Get(id)?.Record(value);

    // ── Tick hooks (driven by World.Tick) ──────────────────────────────────────

    // Once per in-game hour: pull a sample from every sampler-backed stat.
    public void OnSampleTick() {
        for (int i = 0; i < stats.Count; i++) stats[i].Sample();
    }

    // At each day boundary: finalize every stat's in-progress day into history.
    public void OnDayElapsed() {
        for (int i = 0; i < stats.Count; i++) stats[i].FinalizeDay();
    }

    // ── Save / load ─────────────────────────────────────────────────────────────

    public StatSaveData[] GatherSave() {
        var arr = new StatSaveData[stats.Count];
        for (int i = 0; i < stats.Count; i++) arr[i] = stats[i].ToSaveData();
        return arr;
    }

    public void RestoreSave(StatSaveData[] data) {
        if (data == null) return;
        foreach (var sd in data) {
            if (sd == null) continue;
            Get(sd.id)?.LoadFrom(sd);  // unknown id → ignored (save-safe across stat changes)
        }
    }

    // Clear all history (fresh world / LoadDefault).
    public void ClearAll() {
        for (int i = 0; i < stats.Count; i++) stats[i].Clear();
    }
}
