using UnityEngine;

// Shows a "loaded" overlay sprite on a discrete-output processor building (e.g. the scriptorium's
// in-progress book on the desk) whenever its Processor holds a batch — any non-Empty state
// (Filling / Working / Tapped) — and hides it when Empty.
//
// Opt-in by art: a building gets this only if a `{name}_load` sprite exists (see Building ctor).
// Liquid processors (the cauldron) show their batch through the WaterController pot fill instead,
// so they ship no `_load` sprite and never attach this.
//
// Attached from the Building constructor after the Processor is created — NOT AttachAnimations,
// which runs during base() before the processor exists (see Building ctor / Structure.AttachAnimations).
public class ProcessorLoadVisuals : MonoBehaviour {
    Processor processor;
    SpriteRenderer sr;

    // overlaySprite drawn one sorting step above the base building sprite, co-located with it.
    public void Init(Building building, Sprite overlaySprite, int parentSortingOrder) {
        processor = building.processor;
        if (processor == null || overlaySprite == null) { enabled = false; return; }

        GameObject overlay = new GameObject("load_overlay");
        overlay.transform.SetParent(transform, false);
        overlay.transform.localPosition = Vector3.zero;
        sr = SpriteMaterialUtil.AddSpriteRenderer(overlay);
        sr.sprite = overlaySprite;
        sr.flipX = building.mirrored;            // match the base sprite's placement mirror
        sr.sortingOrder = parentSortingOrder + 1; // just above the building body
        LightReceiverUtil.SetSortBucket(sr);
    }

    void Update() {
        if (processor == null || sr == null) return;
        // Idempotent visibility toggle — a batch is "loaded" for every state but Empty.
        bool loaded = processor.state != Processor.State.Empty;
        if (sr.enabled != loaded) sr.enabled = loaded;
    }
}
