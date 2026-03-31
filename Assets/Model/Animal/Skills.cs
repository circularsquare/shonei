// Defines the Skill domain enum and SkillSet — the per-animal container
// for accumulated XP and levels across all skill domains.
//
// XP thresholds double each level: 10 → 20 → 40 → 80 → ...
// Each level grants +5% work speed for that skill domain.
//
// XP is earned during work at a rate of 0.1 per base efficiency unit per tick.
// "Base efficiency" = hunger×sleep × tool bonus (does NOT include the skill bonus
// itself, so skill level doesn't accelerate its own XP gain).

public enum Skill {
    Farming,
    Mining,
    Construction,
    Science,
    Woodworking,
}

public class SkillSet {
    public static readonly int Count = System.Enum.GetValues(typeof(Skill)).Length;

    /// <summary>XP required to advance from <paramref name="level"/> to level+1. Doubles each level: 10, 20, 40, …</summary>
    public static float XpThreshold(int level) => 10f * (1 << level);

    /// <summary>Work speed bonus per level (additive): +5% per level.</summary>
    public const float BonusPerLevel = 0.05f;

    private float[] xp    = new float[Count];
    private int[]   level = new int[Count];

    public float GetXp(Skill skill)    => xp[(int)skill];
    public int   GetLevel(Skill skill) => level[(int)skill];

    /// <summary>Returns the multiplicative work speed bonus for this skill level (e.g. lv2 → 1.10).</summary>
    public float GetBonus(Skill skill) => 1f + GetLevel(skill) * BonusPerLevel;

    /// <summary>
    /// Called each tick when an animal performs work in a skill domain.
    /// Accumulates XP and levels up when thresholds are crossed.
    /// </summary>
    public void GainXp(Skill skill, float amount) {
        if (amount <= 0f) return;
        int s = (int)skill;
        xp[s] += amount;
        while (xp[s] >= XpThreshold(level[s])) {
            xp[s] -= XpThreshold(level[s]);
            level[s]++;
            UnityEngine.Debug.Log($"Skill up! {System.Enum.GetName(typeof(Skill), s)} → lv{level[s]}");
        }
    }

    // ── Save / Load ──────────────────────────────────────────────────────────

    public float[] SerializeXp()    => (float[])xp.Clone();
    public int[]   SerializeLevel() => (int[])level.Clone();

    /// <summary>Restores skill data from save. Handles null (old saves) gracefully — leaves arrays at zero.</summary>
    public void Deserialize(float[] savedXp, int[] savedLevel) {
        if (savedXp    != null) System.Array.Copy(savedXp,    xp,    System.Math.Min(savedXp.Length,    Count));
        if (savedLevel != null) System.Array.Copy(savedLevel, level, System.Math.Min(savedLevel.Length, Count));
    }
}
