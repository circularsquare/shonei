using System;
using UnityEngine;

// Generic frame-by-frame sprite animator. Cycles a SpriteRenderer through `frames` at
// `baseFps`, multiplied by an optional dynamic `speedMultiplier`. Animation is gated on
// `isActive` — when inactive, the current frame is held (not reset), so a stopping
// machine reads as "no longer driven" rather than snapping to a neutral pose.
//
// Used by power buildings (shaft, wheel, windmill, flywheel) — each attaches one of these
// from its Structure.AttachAnimations() override, wiring its own activity callback and
// (where relevant) a speed multiplier sourced from gameplay state (wind, charge, etc.).
//
// Frames are sourced from a sliced Unity sprite sheet via Resources.LoadAll<Sprite>.
// If the building's PNG isn't sliced (or has only one sub-sprite), the helper that
// constructs this component bails out — animation is opt-in per asset.
public class FrameAnimator : MonoBehaviour {
    public Sprite[] frames;
    public Func<bool> isActive;          // null = always active
    public Func<float> speedMultiplier;  // null = constant 1.0
    public float baseFps = 8f;

    SpriteRenderer sr;
    float frameTime;  // accumulated frames-elapsed since the last sprite swap (0..1)
    int frame;

    void Start() {
        sr = GetComponent<SpriteRenderer>();
    }

    void Update() {
        if (frames == null || frames.Length <= 1 || sr == null) return;
        if (isActive != null && !isActive()) return; // hold current frame
        float mult = speedMultiplier != null ? Mathf.Max(0f, speedMultiplier()) : 1f;
        if (mult <= 0f) return;
        frameTime += Time.deltaTime * baseFps * mult;
        if (frameTime >= 1f) {
            int adv = (int)frameTime;
            frame = (frame + adv) % frames.Length;
            frameTime -= adv;
            sr.sprite = frames[frame];
        }
    }
}
