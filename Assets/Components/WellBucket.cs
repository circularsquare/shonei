using UnityEngine;

// Per-frame driver + interpolator for the well's bucket + rope (mirrors ElevatorPlatform). Advances
// the well's draw clock with the frame dt — so the bucket glides smoothly and the raise plays out in
// full — then places the bucket at the model bucketY and sizes the rope from the wellhead down to it.
// Spawned by Well.AttachAnimations on the bucket child GO; the rope SR is a sibling child of the well GO.
public class WellBucket : MonoBehaviour {
    public Well well;
    public SpriteRenderer bucketSr;
    public SpriteRenderer ropeSr;
    public float ropeWidthTiles = 1f / 16f;   // set by Well.AttachAnimations (sampled from the art)

    // Where the rope's bottom sits relative to the bucket centre (negative = above it, near the
    // bucket top where the rope attaches).
    const float RopeBelowBucket = -0.1f;

    void Update() {
        if (well == null) return;
        // Drive the draw with timeScale-scaled deltaTime (covers the same tiles/sec at any game speed,
        // like ElevatorPlatform). May complete the draw (→ Idle) inside this call.
        well.AdvanceDraw(Time.deltaTime);

        // The drawn bucket + rope only show during a draw — at rest, the wellhead sprite's own bucket
        // stands in.
        bool active = well.IsDrawing;
        if (bucketSr != null) bucketSr.enabled = active;
        if (ropeSr   != null) ropeSr.enabled   = active;
        if (!active) return;

        // Bucket sits exactly at the model position (already glided smoothly by AdvanceDraw).
        Vector3 bp = transform.localPosition;
        transform.localPosition = new Vector3(0f, well.bucketY, bp.z);

        // Rope: a solid tinted quad from the top edge of the wellhead tile down past the bucket to its
        // base. Scaling a solid colour is uniform at any length — no tile-gaps or texture stretch —
        // so its bottom edge tracks the bucket exactly 1:1.
        if (ropeSr != null) {
            float top        = well.WellheadLocalY + 0.5f;
            float ropeBottom = well.bucketY - RopeBelowBucket;
            float len        = Mathf.Max(0f, top - ropeBottom);
            Transform rt = ropeSr.transform;
            rt.localScale    = new Vector3(ropeWidthTiles, len, 1f);
            rt.localPosition = new Vector3(0f, top - len * 0.5f, rt.localPosition.z);
        }
    }
}
