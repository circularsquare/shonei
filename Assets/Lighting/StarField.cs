using System.Collections.Generic;
using UnityEngine;

// Sparse twinkling stars on the Sky layer, fading in below twilightFraction.
//
// Stars are children of SkyCamera (parent inherits camera position each frame
// — so stars stay at fixed viewport positions, "pinned to screen"). Each star
// is a small SpriteRenderer driven by a per-instance phase so they twinkle
// out-of-sync.
//
// Like the gradient and clouds, stars use raw colors with no CPU-side ambient
// multiply — the lighting composite handles night dimming. A bright-white
// star with alpha=1 lands at roughly the night ambient color (~5% screen
// brightness), while the multiply-darkened sky behind sits much darker — so
// stars read as glints. If they end up too dim in playtest, escalate to a
// custom emissive shader or move stars to the Unlit layer.
//
// Scene setup:
//   1. Add a child GameObject under SkyCamera, attach this script.
//   2. (Optional) tune starCount, viewport band, scale range.
public class StarField : MonoBehaviour {
    [SerializeField] int   starCount   = 60;
    [SerializeField] int   seed        = 42;

    [Tooltip("Stars only spawn between these viewport-Y values. Leaves the lower band empty for future ground silhouette.")]
    [Range(0f, 1f)] [SerializeField] float bottomY01 = 0.45f;
    [Range(0f, 1f)] [SerializeField] float topY01    = 0.98f;

    [SerializeField] float minScale = 0.04f;
    [SerializeField] float maxScale = 0.08f;

    [SerializeField] int sortingOrder = -50;

    [Tooltip("Must match the cloud SRs' sorting layer (Background) so stars render behind clouds. See SkyGradient for the same rationale.")]
    [SerializeField] string sortingLayerName = "Background";

    [Tooltip("Twinkle speed in radians/sec.")]
    [SerializeField] float twinkleSpeed = 2f;

    [Tooltip("Whole-field rotation rate in degrees/sec. Stars sweep in circles around the screen centre, evoking the celestial-pole rotation real night skies have. ~1.5°/s ≈ 4 min per full rotation; small enough to feel ambient.")]
    [SerializeField] float rotationSpeed = 1.5f;

    [Tooltip("twilightFraction below this value triggers stars (0 = full night, 1 = full day). Stars ramp from 0→full alpha as twilightFraction crosses (this+rampWidth)→this.")]
    [Range(0f, 1f)] [SerializeField] float nightThreshold = 0.1f;
    [Tooltip("Width of the smoothstep ramp around the threshold. Smaller = sharper on/off.")]
    [Range(0f, 0.5f)] [SerializeField] float rampWidth = 0.05f;

    Camera bgCam;
    readonly List<Star> stars = new();
    float rotationRad;  // accumulated rotation angle (radians)

    struct Star {
        public Transform tr;
        public SpriteRenderer sr;
        public Vector2 viewportPos; // (u, v) in [0,1]
        public float phase;         // twinkle offset
    }

    void Start() {
        bgCam = transform.parent != null ? transform.parent.GetComponent<Camera>() : null;
        if (bgCam == null) { Debug.LogError("StarField: parent must be a Camera (SkyCamera). Disabling."); enabled = false; return; }

        // Single shared 1×1 white sprite reused across all stars.
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
        tex.SetPixel(0, 0, Color.white);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(updateMipmaps: false);
        // PPU=1: a unit-scaled SR is 1 world unit; localScale controls visible size.
        var starSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);

        var rng = new System.Random(seed);
        for (int i = 0; i < starCount; i++) {
            var go = new GameObject($"Star{i}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.layer = gameObject.layer;

            var sr = SpriteMaterialUtil.AddSpriteRenderer(go);
            sr.sprite = starSprite;
            sr.sortingLayerName = sortingLayerName;
            sr.sortingOrder = sortingOrder;
            sr.color = new Color(1, 1, 1, 0); // hidden until first LateUpdate

            float scale = Mathf.Lerp(minScale, maxScale, (float)rng.NextDouble());
            go.transform.localScale = new Vector3(scale, scale, 1f);

            stars.Add(new Star {
                tr          = go.transform,
                sr          = sr,
                viewportPos = new Vector2((float)rng.NextDouble(),
                                          Mathf.Lerp(bottomY01, topY01, (float)rng.NextDouble())),
                phase       = (float)(rng.NextDouble() * Mathf.PI * 2f),
            });
        }
    }

    void LateUpdate() {
        if (stars.Count == 0) return;

        // Position depends on bgCam's (zoom-dampened) ortho size + aspect.
        // Recomputed each frame because both can drift with zoom.
        float halfH = bgCam.orthographicSize;
        float halfW = halfH * bgCam.aspect;

        // Day → 0, deep night → 1, with a smooth ramp around `nightThreshold`.
        // (Don't use Mathf.SmoothStep — its signature returns a value between
        // `from` and `to`, NOT a 0..1 weight. Build the smoothstep manually.)
        float u = Mathf.Clamp01((nightThreshold + rampWidth - SunController.twilightFraction) / rampWidth);
        float nightFactor = u * u * (3f - 2f * u);
        float t = Time.time * twinkleSpeed;

        // Rotate the whole field around the screen centre. Apply the rotation
        // by hand (rather than spinning the StarField transform) because we
        // recompute every star's localPosition each frame from its fixed
        // viewportPos — a transform rotation would be cancelled out by the
        // unrotated re-assignment.
        rotationRad += rotationSpeed * Mathf.Deg2Rad * Time.deltaTime;
        float cosR = Mathf.Cos(rotationRad);
        float sinR = Mathf.Sin(rotationRad);

        for (int i = 0; i < stars.Count; i++) {
            var s = stars[i];
            // Map viewport (0..1) → local (-halfW..halfW, -halfH..halfH).
            float lx = (s.viewportPos.x - 0.5f) * (halfW * 2f);
            float ly = (s.viewportPos.y - 0.5f) * (halfH * 2f);
            // Rotate around (0,0) — the screen centre.
            float rx = lx * cosR - ly * sinR;
            float ry = lx * sinR + ly * cosR;
            s.tr.localPosition = new Vector3(rx, ry, 5f);

            float twinkle = 0.6f + 0.4f * Mathf.Sin(t + s.phase);
            float alpha   = nightFactor * twinkle;
            s.sr.color = new Color(1f, 1f, 1f, alpha);
        }
    }
}
