using System.Collections.Generic;
using UnityEngine;

// Cycles a plant's SpriteRenderers between baked wind-sway frames generated
// by PlantBlobFrameBaker. One animator per Plant GameObject — drives the
// anchor SR plus every extension SR off a single time source so all tiles of
// one tree stay coherent (a blob that leans at frame 4 leans across the whole
// tree, not just one tile).
//
// Attached by Plant in its ctor iff a baked frame set exists for the plant.
// Plant rebuilds the (SR → frames) entry list inside UpdateSprite whenever the
// growth stage or extension count changes; this component just walks the list
// each frame and writes the current sprite.
//
// Per-plant time offset (timePhase) desynchronises neighbouring plants — two
// trees standing next to each other animate out of phase, so the scene reads
// as rustling rather than a single global pulse.
public class PlantFrameAnimator : MonoBehaviour {
    // ── tunables (shared across all instances) ───────────────────────────────
    public const int   NumFrames = 24;        // matches PlantBlobFrameBaker
    public const float Fps       = 6f;        // 24 frames / 6 fps = 4 s loop

    // Seconds-domain phase offset. Sampled once in Update, modulo cycle length.
    public float timePhase;

    private struct Entry {
        public SpriteRenderer sr;
        public Sprite[]       frames;
    }
    private readonly List<Entry> entries = new List<Entry>();
    private int lastFrameIdx = -1;

    public void Clear() {
        entries.Clear();
        lastFrameIdx = -1;
    }

    public void Add(SpriteRenderer sr, Sprite[] frames) {
        if (sr == null || frames == null || frames.Length != NumFrames) {
            Debug.LogError($"[PlantFrameAnimator] Bad Add: sr={(sr == null ? "null" : sr.name)}, frames={(frames == null ? "null" : frames.Length.ToString())} (expected {NumFrames})");
            return;
        }
        entries.Add(new Entry { sr = sr, frames = frames });
        // Force a write on the next Update — the new SR otherwise keeps its
        // stale sprite until the frame index next changes.
        lastFrameIdx = -1;
    }

    void Update() {
        int frame = Mathf.FloorToInt((Time.time + timePhase) * Fps) % NumFrames;
        if (frame < 0) frame += NumFrames;
        if (frame == lastFrameIdx) return;
        lastFrameIdx = frame;
        for (int i = 0; i < entries.Count; i++) {
            var e = entries[i];
            if (e.sr != null && e.frames[frame] != null) e.sr.sprite = e.frames[frame];
        }
    }
}
