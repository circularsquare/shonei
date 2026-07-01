using System.Collections.Generic;
using UnityEngine;

// Timed buffs applied to an Animal by drinking tonics. A general container (chosen over per-effect
// decaying fields like Happiness.warmth) so a new tonic effect is just a new BuffType + one query at
// the relevant plug point — no new field plumbing each time.
//
// Plug points today: WorkSpeed → ModifierSystem.GetWorkMultiplier; ColdTolerance / HeatTolerance →
// Happiness.UpdateComfortRange (comfortTempLow / comfortTempHigh); SleepRecovery → Animal.HandleNeeds
// (the eeping.Eep recovery rate).
//
// Persisted across saves as REMAINING duration per buff (see Serialize/Deserialize; SaveSystem
// stores them per animal). Every application AND every load clamps a buff to one full (1-liang)
// dose's worth of time, so an effect can never outlive its intended cap — including a buff carried
// over from an older build with different balance or a pre-cap Apply.
public enum BuffType { WorkSpeed, ColdTolerance, HeatTolerance, SleepRecovery }

public class BuffSet {
    // One active buff. expiresAt is an absolute World.timer (game-seconds) stamp.
    private class Entry {
        public BuffType type;
        public float    magnitude;
        public float    expiresAt;
    }
    private readonly List<Entry> active = new List<Entry>();

    private static float Now => World.instance != null ? World.instance.timer : 0f;

    // A buff may last up to this multiple of a single full (1-liang) dose. The 1.2 headroom keeps
    // the clamp from trimming a legitimately full buff over float/timing jitter, while still catching
    // egregious over-caps like a multi-liang buff persisted from an older build.
    private const float MaxDoseMultiple = 1.2f;

    // Apply or refresh a buff. Re-drinking the same effect resets the timer to whichever expiry is
    // later and keeps the stronger magnitude — so drinking again never weakens or shortens an active
    // effect, and one effect type never stacks into a runaway bonus. Expiry is capped at
    // MaxDurationSeconds (a full dose plus MaxDoseMultiple headroom) so no sequence of drinks — or a
    // stale over-long buff from an older build — can push the effect past ~1.2 liang's grant.
    public void Apply(BuffType type, float magnitude, float durationSeconds) {
        float cap    = Now + MaxDurationSeconds(type);
        float expiry = Mathf.Min(Now + durationSeconds, cap);
        foreach (Entry e in active) {
            if (e.type != type) continue;
            e.expiresAt = Mathf.Min(Mathf.Max(e.expiresAt, expiry), cap);
            e.magnitude = Mathf.Max(e.magnitude, magnitude);
            return;
        }
        active.Add(new Entry { type = type, magnitude = magnitude, expiresAt = expiry });
    }

    // Longest a buff of `type` may last, in game-seconds: the biggest full-dose duration any tonic
    // grants it, times MaxDoseMultiple headroom. Db-derived so it tracks itemsDb without a second
    // source of truth. Returns "no cap" when the answer is unknown (Db not yet loaded, or no tonic
    // grants this effect) so we never zero out a live buff.
    private static float MaxDurationSeconds(BuffType type) {
        float maxDays = 0f;
        if (Db.tonicItems != null)
            foreach (Item t in Db.tonicItems)
                if (t.buffEffect == type) maxDays = Mathf.Max(maxDays, t.buffDuration);
        return maxDays > 0f ? maxDays * World.ticksInDay * MaxDoseMultiple : float.MaxValue;
    }

    // Drops expired buffs. Returns true if anything expired this call, so the caller can refresh any
    // derived state it caches (e.g. the temperature-comfort range).
    public bool Tick() {
        float now = Now;
        int before = active.Count;
        active.RemoveAll(e => now >= e.expiresAt);
        return active.Count != before;
    }

    // Summed magnitude of all active buffs of a type (0 if none). Sum (not max) so a future stacking
    // design works without touching call sites; today one entry per type means it's just that entry.
    public float Total(BuffType type) {
        float sum = 0f;
        foreach (Entry e in active) if (e.type == type) sum += e.magnitude;
        return sum;
    }

    public bool Has(BuffType type) {
        foreach (Entry e in active) if (e.type == type) return true;
        return false;
    }

    // Snapshot of active buffs for display (type, magnitude, remaining game-seconds).
    public IEnumerable<(BuffType type, float magnitude, float remaining)> Active() {
        float now = Now;
        foreach (Entry e in active) yield return (e.type, e.magnitude, e.expiresAt - now);
    }

    // Short player-facing label per effect. ASCII only — the m5x7 UI font has no glyphs for
    // degree signs / symbols, so spell it out.
    public static string Label(BuffType type) {
        switch (type) {
            case BuffType.WorkSpeed:     return "work speed";
            case BuffType.ColdTolerance: return "cold tolerance";
            case BuffType.HeatTolerance: return "heat tolerance";
            case BuffType.SleepRecovery: return "restful sleep";
            default:                     return type.ToString();
        }
    }

    // ── Save / load ──────────────────────────────────────────────────────────
    // Persisted as REMAINING duration (not an absolute expiry) so a buff survives a reload even if
    // the world clock differs across the load.
    public List<BuffSaveData> Serialize() {
        float now = Now;
        var list = new List<BuffSaveData>();
        foreach (Entry e in active) {
            float rem = e.expiresAt - now;
            if (rem > 0f) list.Add(new BuffSaveData { type = e.type, magnitude = e.magnitude, remaining = rem });
        }
        return list;
    }

    public void Deserialize(List<BuffSaveData> saved) {
        active.Clear();
        if (saved == null) return; // old save → no buffs
        float now = Now;
        foreach (BuffSaveData s in saved)
            if (s != null && s.remaining > 0f) {
                // Clamp to one full dose — heals buffs saved under older balance / a pre-cap Apply.
                float rem = Mathf.Min(s.remaining, MaxDurationSeconds(s.type));
                active.Add(new Entry { type = s.type, magnitude = s.magnitude, expiresAt = now + rem });
            }
    }
}

// Serializable form of one active buff: remaining duration in game-seconds (see BuffSet.Serialize).
public class BuffSaveData {
    public BuffType type;
    public float    magnitude;
    public float    remaining;
}
