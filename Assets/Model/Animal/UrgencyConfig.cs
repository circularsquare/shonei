// Central tuning block for the unified urgency task-selection system (Animal.ChooseTask).
// Every idle mouse scores each action category 0..1 each tick and takes the (jittered) argmax;
// these constants shape those scores. Design + rationale: plans/urgency-system.md, SPEC-ai.md.
//
// Plain static (not a ScriptableObject like WorldGenConfig) because these are read on a per-tick,
// per-mouse hot path and aren't preset-swappable content — they're tuning knobs. Edit + recompile
// to tune. Grouped by category to keep the curve legible; all values are first-guess-then-playtested.
//
// ── Urgency landscape (SINGLE SOURCE OF TRUTH) ───────────────────────────────────────────────
// The current achievable range of every category, so changes can be reasoned about against each
// other. SPEC-ai.md points here rather than duplicating these numbers — keep THIS block in sync
// when you retune; it's the one place the whole picture lives.
//   eat     0 → 1.0      (dominates below 0.3 fullness; curve in Eating.HungerUrgency)
//   sleep   0 → 1.0      (gentle; time-shifted threshold — in Eeping.SleepUrgency, not here)
//   work    per tier = TierBase + proximity(≤0.15 at dist 0, halves every 8 tiles) + bonuses
//           (Construct finish +0.10; Water thirst +0–0.10 by dryness):   p1 0.55–0.70
//           p2 0.45–0.60 (construct 0.55–0.70)   p3 0.30–0.45 (water +0–0.10 thirst)   p4 0.25–0.40
//   craft   0.16 → 0.60  (banded; floor clears daytime idle so a needed craft is never soft-locked)
//   drop    0.60 → 0.90  (scales with carried main-inv fullness; below hunger/sleep peaks, above work)
//   equip   0 or 0.45    (only when the tool/clothing slot is empty)
//   leisure 0 → 0.60 (evening) / 0 → 0.15 (day)   (bias × least-satisfied need pull)
//   idle    0.15 (day) / 0.35 (evening)           — the floor low work/leisure must clear
//
// Reasoning aids:
//   • eat/sleep reach ~1.0 and the (1-s) jitter headroom freezes them, so a real need always wins.
//   • leisure (≤0.60) loses to p1 and to p2-near work, so a swamped mouse may skip evening leisure.
//     Acceptable: p1 (haul to unblock a deconstruct) is RARE, and the Gaussian jitter tail is the
//     occasional release valve (≈5% of evening ticks leisure still beats p1-near). Not a soft-lock.
//
// Retuning guidance for future Claudes: keep values legible — round-ish, well-separated bands —
// and update this block in the SAME edit. Reading it is cheaper than re-deriving the work tiers from
// the constants + WorkOrderManager.BestWorkUrgency, so it's worth the upkeep. If it ever drifts from
// the constants below, trust the code.
//
// Plain static (not a ScriptableObject like WorldGenConfig) because these are read on a per-tick,
// per-mouse hot path and aren't preset-swappable content — they're tuning knobs. Edit + recompile to
// tune. Full design + rationale: plans/urgency-system.md, SPEC-ai.md §Task dispatch.
public static class UrgencyConfig {

    // ── Work orders ──────────────────────────────────────────────────
    // Per-order urgency = TierBase[priority-1] + proximityBonus(distance) + per-type bonus.
    public static readonly float[] TierBase = { 0.55f, 0.45f, 0.30f, 0.25f }; // index = priority-1
    public const float ProxWeight = 0.15f;   // max proximity bonus, at distance 0
    public const float ProxFalloff = 8f;     // tiles at which the proximity bonus halves
    public const float FinishBonus = 0.10f;  // "finish what's started" bump for Construct orders
    public const float WaterMaxThirstBonus = 0.10f; // max extra urgency for a bone-dry crop; scales 0→this by how far soil is below moistureMin

    // ── Craft ────────────────────────────────────────────────────────
    // Recipe.Score is unbounded (0..+∞), so craft urgency maps it into a fixed band:
    //   CraftFloor + (CraftCeil - CraftFloor) * s/(1+s).
    // Floor sits just above the daytime idle floor (0.15) so any eligible recipe is never
    // soft-locked out; ceil is the asymptote a scarce-output recipe approaches.
    public const float CraftFloor = 0.16f;
    public const float CraftCeil  = 0.60f;

    // ── Drop carried inventory ───────────────────────────────────────
    // Urgency to dump stale main-inventory carry-over, scaled by how full the main inv is:
    //   DropFloor + (DropCeil - DropFloor) * occupiedStacks/totalStacks.
    // The band sits below the hunger/sleep peaks (~1.0) so a starving or exhausted mouse eats /
    // sleeps before offloading, but above the work tiers (≤0.70) so a laden idle mouse still drops
    // promptly instead of crawling around with a full pack while there's other work to do.
    public const float DropFloor = 0.60f;
    public const float DropCeil  = 0.90f;

    // ── Hunger curve (Eating.HungerUrgency) ──────────────────────────
    public const float HungerConcavity = 0.6f;     // <1 = steep right after the seek threshold (seek early)
    public const float HungerDominateThreshold = 0.3f; // below this fullness, food dominates all categories
    public const float HungerDominateFloor = 0.8f;  // urgency at the dominate threshold; > realistic work max (~0.70)

    // ── Equip / clothing ─────────────────────────────────────────────
    public const float EquipUrgency = 0.45f; // when the slot is empty; above the idle ceiling

    // ── Leisure ──────────────────────────────────────────────────────
    // Urgency = bias × least-satisfied-need pull. Evening makes leisure competitive with work.
    public const float LeisureBiasEvening = 0.60f;
    public const float LeisureBiasDay = 0.15f;

    // ── Idle ─────────────────────────────────────────────────────────
    // Always-available baseline — the floor low-value work/leisure must clear to be worth doing.
    public const float IdleBaseEvening = 0.35f;
    public const float IdleBaseDay = 0.15f;

    // ── Jitter ───────────────────────────────────────────────────────
    // Headroom-scaled Gaussian noise on every score: s + (1-s) * N(0, JitterStdev). Two-directional
    // (a category can be nudged up or down) and the (1-s) factor keeps urgent scores near-fixed while
    // low scores get real variety. The normal tail means most picks stay near the deterministic order
    // but a mouse occasionally does something well off the obvious choice — and that tail is also the
    // probabilistic release valve that stops a busy mouse from being permanently locked out of leisure.
    public const float JitterStdev = 0.1f;
}
