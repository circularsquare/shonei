// Per-animal "activity" tracking: a recency-weighted record of how a mouse has been
// spending its time lately, grouped into five display buckets. Drives the population
// panel's per-mouse activity bar (the "job load / idle time" metric).
//
// Decaying recent occupancy (NOT lifetime totals): each tick we decay every bucket and
// add to the current state's bucket — the same exponential-decay idiom Happiness
// satisfactions use. So the buckets answer "how is this mouse spending its time lately,"
// not "averaged over its whole life" (a lifetime counter barely moves on an old mouse).
// Half-life ~2 in-game days.
//
// Self-normalizing: adding (1-decay) to the current bucket while multiplying all buckets
// by decay keeps the sum ≈ 1 in steady state, so each bucket IS directly its fraction of
// recent time — the bar reads them straight, no separate normalization pass needed.

public enum ActivityGroup {
    Working,
    Walking,
    Leisure,
    Idle,
    Sleep,
}

public class ActivityTracker {
    public static readonly int Count = System.Enum.GetValues(typeof(ActivityGroup)).Length;

    // ~2 in-game day half-life. Cached: a Math.Pow per tick per mouse is wasteful, and
    // World.ticksInDay is fixed at startup so the factor never changes mid-session.
    private static float _decay = -1f;
    private static float Decay {
        get {
            if (_decay < 0f)
                _decay = (float)System.Math.Pow(0.5, 1.0 / (2.0 * World.ticksInDay));
            return _decay;
        }
    }

    private float[] occupancy = new float[Count];

    // Maps a raw animal state to its display bucket. Falling (involuntary, brief) folds
    // into idle; Traveling (off-screen market journey) folds into walking.
    public static ActivityGroup GroupFor(Animal.AnimalState state) {
        switch (state) {
            case Animal.AnimalState.Working:   return ActivityGroup.Working;
            case Animal.AnimalState.Moving:    return ActivityGroup.Walking;
            case Animal.AnimalState.Traveling: return ActivityGroup.Walking;
            case Animal.AnimalState.Leisuring: return ActivityGroup.Leisure;
            case Animal.AnimalState.Eeping:    return ActivityGroup.Sleep;
            case Animal.AnimalState.Idle:      return ActivityGroup.Idle;
            case Animal.AnimalState.Falling:   return ActivityGroup.Idle;
            default:                           return ActivityGroup.Idle;
        }
    }

    // Called once per tick with the mouse's current state.
    public void Tick(Animal.AnimalState state) {
        float decay = Decay;
        for (int i = 0; i < Count; i++) occupancy[i] *= decay;
        occupancy[(int)GroupFor(state)] += 1f - decay;
    }

    // Fraction of recent time spent in this group, 0–1. The sum over groups is ≈ 1 once
    // warmed up; we still normalize defensively (covers the warm-up transient and a
    // brand-new mouse whose buckets are all zero).
    public float Fraction(ActivityGroup g) {
        float sum = 0f;
        for (int i = 0; i < Count; i++) sum += occupancy[i];
        return sum > 0f ? occupancy[(int)g] / sum : 0f;
    }

    // ── Save / Load ──────────────────────────────────────────────────────────

    public float[] Serialize() => (float[])occupancy.Clone();

    // Length-clamped copy in case the group count ever changes between saves.
    public void Deserialize(float[] saved) {
        if (saved == null) return;
        System.Array.Copy(saved, occupancy, System.Math.Min(saved.Length, Count));
    }
}
