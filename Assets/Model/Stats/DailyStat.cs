using System;

// One tracked colony metric, recorded as a historic series of per-day values.
//
// Each stat keeps a fixed-capacity ring buffer of FINALIZED daily values (oldest
// → newest) plus the accumulator for the day currently in progress. At the day
// boundary StatsTracker calls FinalizeDay(), which pushes the in-progress value
// into history and resets the accumulator for the next day.
//
// Two aggregation modes decide how the day's samples collapse into one value:
//   • Sum     — daily value is the total of everything recorded that day
//               (e.g. food points produced).
//   • Average — daily value is the mean of the day's samples
//               (e.g. average social satisfaction).
//
// A stat is fed in one of two ways:
//   • Push  — game code calls Record(value) when an event happens (food output).
//   • Pull  — the stat carries a `sampler` lambda that StatsTracker invokes on a
//             fixed cadence (Sample()); returning null skips that tick (e.g. no
//             animals to average). A pull stat needs no other wiring — register
//             it with a sampler and it tracks itself.
public class DailyStat {
    public enum Agg { Sum, Average }

    // ── Identity ────────────────────────────────────────────────────────────
    public readonly string id;        // stable key — used for save matching and lookup
    public readonly string label;     // display label (player-facing, concise)
    public readonly Agg    agg;
    public readonly Func<float?> sampler; // pull-source; null = push-only stat

    // ── History (ring buffer of finalized daily values, oldest → newest) ─────
    readonly float[] hist;
    int head;   // index of the next write slot
    int count;  // number of valid entries (≤ hist.Length)

    // ── In-progress day accumulator ──────────────────────────────────────────
    float curSum;
    int   curCount;

    public DailyStat(string id, string label, Agg agg, int capacity, Func<float?> sampler = null) {
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("DailyStat needs an id");
        this.id      = id;
        this.label   = label;
        this.agg     = agg;
        this.sampler = sampler;
        hist = new float[Math.Max(1, capacity)];
    }

    public int Capacity => hist.Length;
    public int DayCount => count;

    // The value the day-in-progress would finalize to right now. Sum → running
    // total; Average → mean of samples so far (0 when nothing recorded yet).
    public float CurrentDayValue => agg == Agg.Average
        ? (curCount > 0 ? curSum / curCount : 0f)
        : curSum;

    // ── Recording ─────────────────────────────────────────────────────────────

    // Add a sample/event to the current day. For Sum the values add up; for
    // Average each call is one sample contributing to the day's mean.
    public void Record(float value) {
        curSum   += value;
        curCount += 1;
    }

    // Pull one sample from the sampler (no-op for push stats, or when the sampler
    // returns null). Called by StatsTracker on its sampling cadence.
    public void Sample() {
        if (sampler == null) return;
        float? v = sampler();
        if (v.HasValue) Record(v.Value);
    }

    // Close out the current day: push its finalized value into history and reset
    // the accumulator for the next day.
    public void FinalizeDay() {
        Push(CurrentDayValue);
        curSum   = 0f;
        curCount = 0;
    }

    void Push(float v) {
        hist[head] = v;
        head = (head + 1) % hist.Length;
        if (count < hist.Length) count++;
    }

    // ── Reading ─────────────────────────────────────────────────────────────────

    // Returns up to `maxDays` daily values, oldest → newest, for charting. When
    // includeCurrentDay is true the in-progress day is appended as the final
    // (live) value, so a fresh colony shows something before its first day ends.
    public float[] GetSeries(int maxDays, bool includeCurrentDay) {
        float[] finalized = ToArray();
        int total = finalized.Length + (includeCurrentDay ? 1 : 0);
        int take = Math.Min(maxDays, total);
        var result = new float[take];

        // Fill from the end (newest) backwards so we keep the most recent `take`.
        int w = take - 1;
        if (includeCurrentDay && w >= 0) { result[w--] = CurrentDayValue; }
        for (int i = finalized.Length - 1; i >= 0 && w >= 0; i--) result[w--] = finalized[i];
        return result;
    }

    // ── Save / load ─────────────────────────────────────────────────────────────

    // Finalized history, oldest → newest. Length == DayCount.
    public float[] ToArray() {
        var result = new float[count];
        int start = (head - count + hist.Length) % hist.Length;
        for (int i = 0; i < count; i++) result[i] = hist[(start + i) % hist.Length];
        return result;
    }

    public StatSaveData ToSaveData() {
        return new StatSaveData {
            id           = id,
            history      = ToArray(),
            currentSum   = curSum,
            currentCount = curCount,
        };
    }

    // Replace contents from save. `history` is treated as oldest → newest.
    public void LoadFrom(StatSaveData sd) {
        head = 0; count = 0; curSum = 0f; curCount = 0;
        if (sd == null) return;
        if (sd.history != null) foreach (float v in sd.history) Push(v);
        curSum   = sd.currentSum;
        curCount = sd.currentCount;
    }

    // Clear all history and the in-progress day (fresh world / LoadDefault).
    public void Clear() {
        head = 0; count = 0; curSum = 0f; curCount = 0;
    }
}
