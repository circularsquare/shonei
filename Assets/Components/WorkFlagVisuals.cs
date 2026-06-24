using UnityEngine;

// Renders a work flag as a static sprite whose pose reflects wind strength, flipped to face
// downwind. No animation — a still flag read better than the ripple cycle.
//
// The sheet is 3 rows × 4 frames (low / medium / high wind, top to bottom); we show the first
// frame of the row picked by |wind|:
//   |wind| < 0.1     -> low
//   0.1 .. 0.3       -> medium
//   > 0.3            -> high
// Boundaries carry a small hysteresis band so a wind hovering on a threshold doesn't flicker
// between poses.
//
// Facing: the art points right (correct for rightward wind). When wind blows left we flipX —
// but a naive flipX mirrors about the sprite pivot (tile centre, art-x 8), and the pole sits
// at art-x 7.5 (the 8th pixel). Mirroring about the pivot would shove the pole 1 px right, so
// a flipped flag is nudged 1 px (1/16 world unit, PPU 16) left to put the mirror axis back on
// the pole — the pole column stays fixed across the flip, and the nudge is a whole pixel so it
// stays pixel-grid aligned.
//
// Attached by WorkFlag.AttachAnimations().
public class WorkFlagVisuals : MonoBehaviour {
    public Sprite[] frames;   // 3 tiers × N frames, slice order (row-major: low, med, high)

    const int Tiers = 3;

    // |wind| boundaries for stepping the pose up vs down — the gap between them is the
    // hysteresis band. Wind is ~±0.4 typical, gusting higher (see WeatherSystem).
    static readonly float[] TierUp   = { 0.1f, 0.3f };    // low→med, med→high
    static readonly float[] TierDown = { 0.08f, 0.27f };  // med→low, high→med

    // Wind below this magnitude holds the current facing, so the flag doesn't snap back and
    // forth as wind drifts across zero.
    const float FlipDeadzone = 0.02f;

    // Leftward nudge applied while flipped, to mirror about the pole (art-x 7.5) rather than
    // the pivot (art-x 8). 1 art-pixel at PPU 16.
    const float FlipOffset = 1f / 16f;

    SpriteRenderer sr;
    float baseX;          // unflipped local x, captured once
    int framesPerTier;
    int tier;
    bool facingLeft;

    void Start() {
        sr = GetComponent<SpriteRenderer>();
        baseX = transform.localPosition.x;
        if (frames != null && frames.Length >= Tiers && frames.Length % Tiers == 0) {
            framesPerTier = frames.Length / Tiers;
        } else {
            Debug.LogError($"WorkFlagVisuals: expected a frame count divisible by {Tiers} tiers, got {frames?.Length ?? 0}");
            enabled = false;
        }
    }

    void Update() {
        if (sr == null) return;
        float w   = WeatherSystem.instance?.wind ?? 0f;
        float mag = Mathf.Abs(w);

        // Facing (deadzone holds the current facing through dead-calm).
        if (w > FlipDeadzone) facingLeft = false;
        else if (w < -FlipDeadzone) facingLeft = true;

        // Strength tier — step at most one per frame; wind is smoothed and slow.
        if (tier < Tiers - 1 && mag >= TierUp[tier]) tier++;
        else if (tier > 0 && mag < TierDown[tier - 1]) tier--;

        // Set idempotently each frame — flipX also overrides any mirror flip from placement,
        // since the flag's facing is wind-driven, not placement-driven.
        sr.sprite = frames[tier * framesPerTier];
        sr.flipX = facingLeft;
        float wantX = facingLeft ? baseX - FlipOffset : baseX;
        Vector3 p = transform.localPosition;
        if (p.x != wantX) {
            p.x = wantX;
            transform.localPosition = p;
        }
    }
}
