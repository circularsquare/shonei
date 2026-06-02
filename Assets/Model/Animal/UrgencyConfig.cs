// Central tuning block for the unified urgency task-selection system (Animal.ChooseTask).
// Every idle mouse scores each action category 0..1 each tick and takes the (jittered) argmax;
// these constants shape those scores. Design + rationale: plans/urgency-system.md, SPEC-ai.md.
//
// Plain static (not a ScriptableObject like WorldGenConfig) because these are read on a per-tick,
// per-mouse hot path and aren't preset-swappable content — they're tuning knobs. Edit + recompile
// to tune. Grouped by category to keep the curve legible; all values are first-guess-then-playtested.
//
// Rough urgency landscape these produce (so changes can be reasoned about against each other):
//   eat   0 → 1.0   (dominates below 0.3 fullness)
//   work  0 → ~0.70 (p1/construct at distance 0)
//   craft 0 → 0.6   (asymptotic; a balanced recipe ≈ 0.30)
//   sleep 0 → 1.0   (gentle; time-shifted threshold — lives in Eeping, not here)
//   equip 0 or 0.45
//   leisure 0 → 0.50 (evening) / 0 → 0.12 (day)
//   idle  0.15 (day) / 0.35 (evening) — the floor low work/leisure must clear
public static class UrgencyConfig {

    // ── Work orders ──────────────────────────────────────────────────
    // Per-order urgency = TierBase[priority-1] + proximityBonus(distance) + finishBonus(type).
    public static readonly float[] TierBase = { 0.55f, 0.45f, 0.30f, 0.20f }; // index = priority-1
    public const float ProxWeight = 0.15f;   // max proximity bonus, at distance 0
    public const float ProxFalloff = 8f;     // tiles at which the proximity bonus halves
    public const float FinishBonus = 0.10f;  // "finish what's started" bump for Construct orders

    // ── Craft ────────────────────────────────────────────────────────
    // Recipe.Score is unbounded multiplicative, so craft urgency = CraftWeight * s/(1+s).
    public const float CraftWeight = 0.6f;

    // ── Hunger curve (Eating.HungerUrgency) ──────────────────────────
    public const float HungerConcavity = 0.6f;     // <1 = steep right after the seek threshold (seek early)
    public const float HungerDominateThreshold = 0.3f; // below this fullness, food dominates all categories
    public const float HungerDominateFloor = 0.8f;  // urgency at the dominate threshold; > realistic work max (~0.70)

    // ── Equip / clothing ─────────────────────────────────────────────
    public const float EquipUrgency = 0.45f; // when the slot is empty; above the idle ceiling

    // ── Leisure ──────────────────────────────────────────────────────
    // Urgency = bias × least-satisfied-need pull. Evening makes leisure competitive with work.
    public const float LeisureBiasEvening = 0.50f;
    public const float LeisureBiasDay = 0.12f;

    // ── Idle ─────────────────────────────────────────────────────────
    // Always-available baseline — the floor low-value work/leisure must clear to be worth doing.
    public const float IdleBaseEvening = 0.35f;
    public const float IdleBaseDay = 0.15f;

    // ── Jitter ───────────────────────────────────────────────────────
    // Headroom-scaled randomness on every score: s + (1-s) * JitterStrength * rand. Urgent scores
    // barely move (deterministic), low scores get variety (a chill mouse varies its pick).
    public const float JitterStrength = 0.15f;
}
