using UnityEngine;

// Procedurally rasterizes a cloud sprite from a cluster of overlapping
// 3D metaball spheres. Blob centers are importance-sampled from an
// `envelope × Perlin noise` weight field so they clump organically inside
// the canvas; blob radii scale with the local envelope value so the
// biggest puffs sit at the cluster's heart. Many small blobs (currently
// ~5x what a line-based placement would use) give the Perlin field
// plenty of material to shape into a cumulus-like cluster. The visible
// shape is the iso-surface of the summed 3D density field, raymarched
// per pixel, and shaded via a procedural normal map derived from the
// field's gradient — giving a rounded volumetric look without any
// hand-authored art.
//
// ── Sizing ─────────────────────────────────────────────────────────────
// Size variety is baked into the sprite at generation time, not added later
// via SpriteRenderer.localScale — scaling pixel art at non-integer factors
// shimmers under camera pan with Point filtering. Callers pre-generate a
// pool spanning sizeRank ∈ [0, 1] and select by humidity at spawn.
//
// ── 3D density + raymarched iso-surface ───────────────────────────────
// Each blob is a 3D sphere at `(cx, cy, 0)` of radius r. Density at any
// 3D point is the sum of `max(0, 1 - dist²/r²)` over every blob — the
// classic metaball field. Overlap regions naturally fill cracks between
// blobs (sum > 1) without any cap, because we never read the summed
// values directly — we extract a single iso-surface from the field.
//
// For each 2D pixel we march the z axis from `+R` toward `0` and find
// the front-facing surface where density crosses `IsoLevel` (sub-pixel
// linear interp on the crossing step). That gives a 3D surface point
// `(px, py, surfaceZ)` per pixel.
//
// The shading normal is `-normalize(∇density)` at that surface point,
// computed analytically — each blob contributes `(-2·dx, -2·dy, -2·z) / r²`
// to the gradient. The resulting normal is the proper 3D iso-surface
// normal: at cluster tops it points at the viewer; at the silhouette it
// lies in the surface plane; in between it follows the smoothly curved
// metaball surface, which avoids the wobble you get from summing 2D
// height-field contributions per blob.
//
// ── Silhouette + bottom shadow ─────────────────────────────────────────
// Binary alpha: opaque white where density ≥ threshold, transparent below.
// Soft-edged sprites read blurry against the sky, so we keep crisp 1px
// pixel-art edges and rely on the normal map for volumetric shading. The
// bottom row of the silhouette (pixels above transparency) gets a slight
// bluish tint so the cloud reads as "sitting" on a base, not floating
// abstractly. Top of the cloud stays pure white.
//
// ── Normal map ─────────────────────────────────────────────────────────
// `n = -normalize(∇density)` at the per-pixel surface point, encoded
// `(n*0.5+0.5)` into RGB. Because the gradient comes from the 3D field
// at a 3D surface point (rather than a per-blob 2D height function),
// overlapping blobs blend into a single smooth metaball surface, and
// the lighting reads as one volume rather than several stacked discs.
//
// ── Bound via MaterialPropertyBlock ────────────────────────────────────
// Unity's auto-binding of sprite secondary textures only works for imported
// assets, not Sprite.Create-d ones. Callers apply the normal map via the
// SpriteRenderer's property block using GetPropertyBlock → SetTexture →
// SetPropertyBlock — see CloudLayer. A fresh `new MaterialPropertyBlock()`
// would leave _MainTex unset, which kills NormalsCapture's alpha clip and
// fills the sprite bounding box with sprite-lit lighting (visible as a
// rectangular halo around each cloud).
//
// ── Memory ─────────────────────────────────────────────────────────────
// Each result owns TWO Texture2Ds (main + normal). Destroy both when done.
public static class CloudSpriteGenerator {
    // Result bundle: the sprite (with its main texture) and its matching
    // normal map. Callers apply the normal map via the SpriteRenderer's
    // property block; see CloudLayer for the canonical pattern (use
    // GetPropertyBlock first so Unity's auto-binding of _MainTex per
    // renderer is preserved — building a fresh MPB from scratch can leave
    // _MainTex on the material's "white" fallback, which kills the
    // NormalsCapture alpha clip and fills the sprite's whole bounding box
    // with sprite-lit lighting).
    public readonly struct Result {
        public readonly Sprite sprite;
        public readonly Texture2D normalMap;

        public Result(Sprite sprite, Texture2D normalMap) {
            this.sprite = sprite;
            this.normalMap = normalMap;
        }
    }

    public static readonly int NormalMapId = Shader.PropertyToID("_NormalMap");

    // Iso-level for the 3D metaball surface. Higher = tighter, more
    // separated blob shapes; lower = silhouette closer to the union of
    // disk footprints + stronger metaball bridging between adjacent
    // blobs. 0.5 is a balanced middle.
    const float IsoLevel = 0.5f;

    // Sub-pixel step used when raymarching the z axis to find the surface
    // crossing. Smaller = smoother sub-pixel z but more work per pixel.
    const float ZStep = 0.5f;

    // Bottom-edge shadow tint. Subtle cool tint rather than darker grey
    // because the lighting composite multiplies by ambient×sun — pure grey
    // ends up muddy at sunset, while a slight blue stays distinct.
    static readonly Color Body   = new Color(1f, 1f, 1f, 1f);
    static readonly Color Shadow = new Color(0.82f, 0.86f, 0.92f, 1f);

    // Generate a single cloud sprite + normal map. `sizeRank` 0=small wispy,
    // 1=big cumulus.
    public static Result Generate(System.Random rng, float sizeRank) {
        sizeRank = Mathf.Clamp01(sizeRank);

        // Primary size lever: blob radius. Big end goes up to ~15px so the
        // largest cumulus reads as substantial; small end stays modest for
        // distant wispy clouds.
        float blobR = Mathf.Lerp(7f, 15f, sizeRank) + (float)rng.NextDouble() * 0.5f;

        // 5x more blobs than the previous line-based placement. Perlin
        // is now doing all the spatial structure on its own, so we feed
        // it plenty of small blobs to cluster.
        int blobs = Mathf.RoundToInt(Mathf.Lerp(25f, 45f, sizeRank)) + rng.Next(-3, 4);
        blobs = Mathf.Clamp(blobs, 20, 50);

        // Canvas dimensions are derived from blobR alone, not blob count,
        // because Perlin-clustered placement spreads blobs across the
        // available area rather than along a fixed centerline.
        const int pad = 2;
        int w = Mathf.CeilToInt(5f * blobR + 2f * pad + 2f);
        int h = Mathf.CeilToInt(2.5f * blobR + 2f * pad + 2f);

        // === Importance-sampled blob placement =========================
        // Blob centers are drawn from a 2D probability field:
        //     weight(x, y) = envelope(x, y) × perlin(x, y)
        // where the envelope is a separable bell that falls to 0 at the
        // canvas edges, and the Perlin term clumps blobs into the noise's
        // high regions for organic, cloud-like clustering. We dart-throw
        // candidate (cx, cy) pairs and accept with probability `weight`.
        //
        // Each blob's radius is modulated by the local envelope value so
        // the biggest blobs sit in the canvas interior and smaller puffs
        // appear at the edges — that's what gives the silhouette its
        // tapered outline without a hand-coded taper schedule.
        float noiseOffX = (float)(rng.NextDouble() * 1000.0);
        float noiseOffY = (float)(rng.NextDouble() * 1000.0);
        const float NoiseScale = 0.07f;
        const int MaxAttempts = 80;

        var centers = new BlobCenter[blobs];
        for (int i = 0; i < blobs; i++) {
            float cx = w * 0.5f, cy = h * 0.5f;
            for (int attempt = 0; attempt < MaxAttempts; attempt++) {
                float candX = (float)(rng.NextDouble() * w);
                float candY = (float)(rng.NextDouble() * h);
                float xn = candX / w, yn = candY / h;
                float ex = 1f - (2f * xn - 1f) * (2f * xn - 1f);
                float ey = 1f - (2f * yn - 1f) * (2f * yn - 1f);
                float envelope = ex * ey;
                float noise = Mathf.PerlinNoise(candX * NoiseScale + noiseOffX, candY * NoiseScale + noiseOffY);
                if ((float)rng.NextDouble() < envelope * noise) {
                    cx = candX; cy = candY;
                    break;
                }
            }

            // Blob radius scales with the local envelope value — bigger
            // blobs in the dense interior, smaller puffs at the edges.
            float xn2 = cx / w, yn2 = cy / h;
            float bellHere = (1f - (2f * xn2 - 1f) * (2f * xn2 - 1f))
                           * (1f - (2f * yn2 - 1f) * (2f * yn2 - 1f));
            float cr = blobR * Mathf.Lerp(0.5f, 1f, bellHere) * (0.85f + 0.15f * (float)rng.NextDouble());

            // Keep the disk inside the canvas. Flat-bottom snap is
            // intentionally absent — Perlin alone decides cy, so the
            // cloud's underside floats wherever the noise drops it.
            cx = Mathf.Clamp(cx, cr + pad + 1f, w - cr - pad - 1f);
            cy = Mathf.Clamp(cy, cr + pad + 1f, h - cr - pad - 1f);

            centers[i] = new BlobCenter(cx, cy, cr);
        }

        // Conservative z-march start: just above the largest possible
        // blob center, so the first density evaluation is guaranteed to
        // sit above all blobs (density = 0 there).
        float zStart = blobR + 2f;

        // First pass: for each pixel, raymarch z from `zStart` toward 0
        // and find the front-facing surface where 3D density crosses
        // `IsoLevel`. Sub-step linear interp gives sub-pixel z precision.
        // We also remember silhouette membership for the body/shadow pass.
        bool[]  inside   = new bool[w * h];
        float[] surfaceZ = new float[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int idx = y * w + x;
                float pxF = x + 0.5f, pyF = y + 0.5f;

                // Quick silhouette test: the density profile in z (at fixed
                // xy) is unimodal with its peak at z = 0 (blobs all live
                // on that plane), so if peak < IsoLevel the ray never
                // crosses the surface and we can skip the march entirely.
                float peak = SampleDensity3D(pxF, pyF, 0f, centers);
                if (peak < IsoLevel) { inside[idx] = false; continue; }
                inside[idx] = true;

                // March +z → 0 looking for the first crossing into the
                // iso-surface. `prev` starts at 0 (density at zStart
                // assumed below IsoLevel by construction).
                float prev = 0f;
                for (float z = zStart; z >= 0f; z -= ZStep) {
                    float d = SampleDensity3D(pxF, pyF, z, centers);
                    if (d >= IsoLevel) {
                        surfaceZ[idx] = z + ZStep * (IsoLevel - prev) / Mathf.Max(d - prev, 1e-6f);
                        break;
                    }
                    prev = d;
                }
            }
        }

        // Second pass: binary silhouette pixels, with the bottom row of
        // the silhouette (pixels with no silhouette pixel directly below)
        // tinted by the cool shadow color. Unity texture y=0 is the
        // bottom row.
        var pixels = new Color[w * h];
        var clear = new Color(0, 0, 0, 0);
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int idx = y * w + x;
                if (!inside[idx]) { pixels[idx] = clear; continue; }
                bool hasBelow = y > 0 && inside[(y - 1) * w + x];
                pixels[idx] = hasBelow ? Body : Shadow;
            }
        }

        // Third pass: normal map = `-normalize(∇density)` at each pixel's
        // 3D surface point. The gradient is computed analytically:
        // each blob contributes `(-2·dx, -2·dy, -2·z) / r²` (and zero
        // outside its radius), summed across blobs.
        var normPixels = new Color[w * h];
        var emptyNormal = new Color(0.5f, 0.5f, 1f, 0f);
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int idx = y * w + x;
                if (!inside[idx]) { normPixels[idx] = emptyNormal; continue; }
                float pxF = x + 0.5f, pyF = y + 0.5f;
                Vector3 grad = SampleGradient3D(pxF, pyF, surfaceZ[idx], centers);
                float mag = grad.magnitude;
                Vector3 n = mag > 1e-6f ? -grad / mag : new Vector3(0f, 0f, 1f);
                normPixels[idx] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z, 1f);
            }
        }

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: false);

        // Normal map is sampled by NormalsCapture as a tangent-space normal.
        // Keep it Point-filtered so the per-pixel normals don't blur into
        // each other (and the soft alpha border stays in lock-step with the
        // normal it carries).
        var normalTex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
        normalTex.filterMode = FilterMode.Point;
        normalTex.wrapMode = TextureWrapMode.Clamp;
        normalTex.SetPixels(normPixels);
        normalTex.Apply(updateMipmaps: false);

        // PPU=16 matches the hand-authored cloud sprites.
        var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), pixelsPerUnit: 16f);

        return new Result(sprite, normalTex);
    }

    readonly struct BlobCenter {
        public readonly float x, y, r;
        public BlobCenter(float x, float y, float r) { this.x = x; this.y = y; this.r = r; }
    }

    // Summed 3D metaball density at `(x, y, z)`. Blobs sit at `(c.x, c.y, 0)`,
    // so a blob's contribution at this point is `max(0, 1 - (dx² + dy² + z²)/r²)`.
    // The summed field is what the raymarch extracts an iso-surface from.
    static float SampleDensity3D(float x, float y, float z, BlobCenter[] centers) {
        float sum = 0f;
        for (int b = 0; b < centers.Length; b++) {
            var c = centers[b];
            float dx = x - c.x, dy = y - c.y;
            float distSq = dx * dx + dy * dy + z * z;
            float rSq = c.r * c.r;
            if (distSq < rSq) sum += 1f - distSq / rSq;
        }
        return sum;
    }

    // Analytical gradient of the summed density at `(x, y, z)`. Each blob's
    // contribution `1 - dist²/r²` differentiates to `(-2·dx, -2·dy, -2·z)/r²`
    // inside its disk, zero outside. Used to derive the surface normal:
    // `n = -normalize(grad)` points outward from the cloud volume.
    static Vector3 SampleGradient3D(float x, float y, float z, BlobCenter[] centers) {
        float gx = 0f, gy = 0f, gz = 0f;
        for (int b = 0; b < centers.Length; b++) {
            var c = centers[b];
            float dx = x - c.x, dy = y - c.y;
            float distSq = dx * dx + dy * dy + z * z;
            float rSq = c.r * c.r;
            if (distSq < rSq) {
                float invRSq = 1f / rSq;
                gx -= 2f * dx * invRSq;
                gy -= 2f * dy * invRSq;
                gz -= 2f * z  * invRSq;
            }
        }
        return new Vector3(gx, gy, gz);
    }
}
