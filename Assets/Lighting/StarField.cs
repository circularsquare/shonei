using System.Collections.Generic;
using UnityEngine;

// Sparse twinkling stars on the Sky layer, fading in below twilightFraction
// and slowly rotating around the screen centre.
//
// Stars are children of SkyCamera (parent inherits camera position each frame
// — so stars stay anchored to the camera frustum, "pinned to screen"). Each
// star's position is stored as a (x, y) in a unit disk centred at origin,
// then scaled to the frustum's half-diagonal radius each frame. A disk that
// inscribes the screen rectangle's corners fully covers the screen at any
// rotation angle, so rotation never reveals empty corners.
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
//   2. (Optional) tune starCount, twinkle/rotation speed, night threshold.
public class StarField : MonoBehaviour {
    [SerializeField] int   starCount   = 100;
    [SerializeField] int   seed        = 42;

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
        public Vector2 unitDiskPos; // (x, y) in disk of radius 1, centred at origin
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
            // Rejection-sample a point in the unit disk: pick from [-1, 1]²,
            // reject if outside. Uniform over the disk (unlike polar (r, θ)
            // sampling which clusters near the centre).
            float ux, uy;
            do {
                ux = (float)(rng.NextDouble() * 2.0 - 1.0);
                uy = (float)(rng.NextDouble() * 2.0 - 1.0);
            } while (ux * ux + uy * uy > 1f);

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
                unitDiskPos = new Vector2(ux, uy),
                phase       = (float)(rng.NextDouble() * Mathf.PI * 2f),
            });
        }
    }

    void LateUpdate() {
        if (stars.Count == 0) return;

        // Disk radius = frustum half-diagonal so the rotated disk always fully
        // covers the screen rectangle. Without this, rotating a screen-sized
        // rectangle reveals empty triangles at the corners on every quarter-turn.
        float halfH = bgCam.orthographicSize;
        float halfW = halfH * bgCam.aspect;
        float maxR  = Mathf.Sqrt(halfW * halfW + halfH * halfH);

        // Day → 0, deep night → 1, with a smooth ramp around `nightThreshold`.
        // (Don't use Mathf.SmoothStep — its signature returns a value between
        // `from` and `to`, NOT a 0..1 weight. Build the smoothstep manually.)
        float u = Mathf.Clamp01((nightThreshold + rampWidth - SunController.twilightFraction) / rampWidth);
        float nightFactor = u * u * (3f - 2f * u);
        float t = Time.time * twinkleSpeed;

        // Rotate the whole field around the screen centre. Apply the rotation
        // by hand (rather than spinning the StarField transform) because we
        // recompute every star's localPosition each frame from its fixed
        // unitDiskPos — a transform rotation would be cancelled out by the
        // unrotated re-assignment.
        rotationRad += rotationSpeed * Mathf.Deg2Rad * Time.deltaTime;
        float cosR = Mathf.Cos(rotationRad);
        float sinR = Mathf.Sin(rotationRad);

        for (int i = 0; i < stars.Count; i++) {
            var s = stars[i];
            // Scale unit-disk coords to the frustum half-diagonal radius.
            float wx = s.unitDiskPos.x * maxR;
            float wy = s.unitDiskPos.y * maxR;
            // Rotate around (0,0) — the screen centre.
            float rx = wx * cosR - wy * sinR;
            float ry = wx * sinR + wy * cosR;
            s.tr.localPosition = new Vector3(rx, ry, 5f);

            float twinkle = 0.6f + 0.4f * Mathf.Sin(t + s.phase);
            float alpha   = nightFactor * twinkle;
            s.sr.color = new Color(1f, 1f, 1f, alpha);
        }
    }
}
