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
//           hauler work     = ANY tier the hauler is eligible for, plus HaulerWorkBonus (0.05): p3 ~0.35–0.50
//           non-hauler haul = the p1/p3 haul tier minus NonHaulerHaulPenalty (0.20): ~0.10–0.50
//   craft   0.16 → 0.60  (banded; floor clears daytime idle so a needed craft is never soft-locked)
//   extract 0 or 0.50    (quarry / pit: flat 0.5 while any output wanted, else 0 — not economic-banded)
//   drop    0.40 → 0.90  (scales with carried main-inv fullness; below hunger/sleep peaks, above work)
//           + 0.62 spike when a wanted craft can't fit its inputs for carried clutter (DropCraftBlocked)
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

    // Any mouse may take a Haul order (floor clutter, storage eviction) — canDo is open — but a
    // non-hauler's haul urgency is docked by this. Keeps dedicated haulers prioritized and makes a
    // non-hauler only pitch in on nearby clutter when it would otherwise idle. Applied in BOTH
    // BestWorkUrgency (the work-category score) and ChooseOrder's within-tier ranking, so haul also
    // loses to a non-hauler's own same-tier work (farmer watering, merchant market hauls).
    public const float NonHaulerHaulPenalty = 0.20f;

    // Mirror of the penalty above, for the dedicated hauler. A hauler's entire job IS hauling — floor
    // hauls, fuel supply, blueprint/furnishing supply are all fetch-and-carry — so every order a hauler
    // is eligible for gets this small bump. Purpose: a FAR p3 task (proximity bonus decayed to ~0)
    // scores only ~TierBase[2] (0.30) and would lose to the evening idle floor (IdleBaseEvening 0.35);
    // this lifts it to ~0.35 so the hauler keeps working instead of standing around while haulable work
    // sits across the map. Applied uniformly to all of a hauler's orders in the UrgencyAdjust seam, so
    // it shifts their whole work curve up vs idle WITHOUT changing the within-tier ranking among them.
    public const float HaulerWorkBonus = 0.05f;

    // HaulFromMarket (pickup of market excess) shares the p3 tier with HaulToMarket (delivery) and
    // open-to-all floor hauls. This dock ranks a pickup just BELOW a delivery — so merchants still
    // exhaust deliveries first and piggyback pickups on the return leg — while keeping it ABOVE a
    // non-hauler's floor haul (which is docked the larger NonHaulerHaulPenalty). Both market orders
    // carry no getDistance (fixed proximity), so this yields a clean hard ordering with no distance
    // edge-cases. Must stay in (0, NonHaulerHaulPenalty) for that ordering to hold.
    public const float MarketPickupDock = 0.10f;

    // ── Craft ────────────────────────────────────────────────────────
    // Recipe.Score is unbounded (0..+∞), so craft urgency maps it into a fixed band:
    //   CraftFloor + (CraftCeil - CraftFloor) * s/(1+s).
    // Floor sits just above the daytime idle floor (0.15) so any eligible recipe is never
    // soft-locked out; ceil is the asymptote a scarce-output recipe approaches.
    public const float CraftFloor = 0.16f;
    public const float CraftCeil  = 0.60f;

    // Maps an unbounded economic need score `s` (0..+∞, as from Recipe.Score / Foundry.TargetNeedScore)
    // into the craft urgency band via s/(1+s). s<=0 → 0 (no pull); +∞ (a never-produced output) saturates
    // to CraftCeil (the "make it now" signal — guards the old NaN trap). Shared by crucible CRAFT
    // (Animal.CraftUrgency) and FOUNDRY feed/cast urgency, so both compete on the SAME economic footing.
    public static float CraftBand(float s) {
        if (s <= 0f) return 0f;
        if (float.IsInfinity(s)) return CraftCeil;
        return CraftFloor + (CraftCeil - CraftFloor) * (s / (1f + s));
    }

    // Extraction (quarry / digging pit) uses a FLAT urgency instead of economic scarcity: players keep
    // mining for rare drops even when the base material is over target, so it pulls at a constant rate
    // until every possible output is over target (gated by Recipe.ExtractionWanted). Sits below the
    // evening-leisure ceiling (0.60) and the hunger/sleep peaks, above idle and non-hauler hauls.
    public const float ExtractionUrgency = 0.5f;
    // The economic-score value that maps to ExtractionUrgency through CraftBand, so extraction recipes
    // ride the same scored-recipe pipeline as normal crafts with no special case downstream. Inverts
    // CraftBand: U = Floor + (Ceil-Floor)·s/(1+s)  →  s = (U-Floor)/(Ceil-U).
    public static float ExtractionScoreForBand() => (ExtractionUrgency - CraftFloor) / (CraftCeil - ExtractionUrgency);

    // ── Drop carried inventory ───────────────────────────────────────
    // Urgency to dump stale main-inventory carry-over, scaled by how full the main inv is:
    //   DropFloor + (DropCeil - DropFloor) * occupiedStacks/totalStacks.
    // The band sits below the hunger/sleep peaks (~1.0) so a starving or exhausted mouse eats /
    // sleeps before offloading. The FLOOR is deliberately low so a mouse carrying just a stack or two
    // keeps working (a producer like a miner accumulates several rounds before hauling — drop only
    // overtakes its 0.5 work urgency once the pack is ~20% full); the CEIL still forces a full pack to
    // offload promptly. The old gap where clutter below this floor could starve a multi-input craft
    // of fetch slots is now closed by the DropCraftBlocked spike below + CraftTask's carry cap.
    public const float DropFloor = 0.40f;
    public const float DropCeil  = 0.90f;

    // Spike urgency when a craft the mouse wants can't fit its inputs because of carried clutter
    // (Animal.CraftBlockedByClutter). Sits just above the craft ceiling (0.60) so the mouse clears
    // the pack before re-attempting the craft, but below the hunger/sleep peaks (~1.0) and near-p1
    // work — so genuine priorities still win, and the craft simply waits a beat.
    public const float DropCraftBlocked = 0.62f;

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

    // ── Tonic drinking (timed buffs) ─────────────────────────────────
    // Vigor / restful tonics are "always eligible": a small baseline just above the daytime idle
    // floor (0.15), so an idle mouse with one in stock drinks it but it never preempts real work or
    // needs. Temperature tonics (warming / cooling) are need-driven instead — urgency scales with how
    // far the mouse is outside its comfort band, mapping TonicTempSpan °C of deviation to TonicTempCeil.
    public const float TonicBaseline = 0.18f;
    public const float TonicTempCeil = 0.80f; // below the eat/sleep ~1.0 peaks, above most work tiers
    public const float TonicTempSpan = 10f;   // °C outside comfort that maps to the ceiling

    // ── Jitter ───────────────────────────────────────────────────────
    // Headroom-scaled Gaussian noise on every score: s + (1-s) * N(0, JitterStdev). Two-directional
    // (a category can be nudged up or down) and the (1-s) factor keeps urgent scores near-fixed while
    // low scores get real variety. The normal tail means most picks stay near the deterministic order
    // but a mouse occasionally does something well off the obvious choice — and that tail is also the
    // probabilistic release valve that stops a busy mouse from being permanently locked out of leisure.
    public const float JitterStdev = 0.1f;
}
