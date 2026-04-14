using System.Collections.Generic;
using UnityEngine;

// Sky light: emitted from sky-exposed tiles (!hasBackground), falls off with
// distance into nearby material. Encoded as a per-tile R8 texture (_SkyExposureTex)
// with bilinear filtering for smooth sub-tile gradients. Distance computed via
// multi-source BFS flood fill (Chebyshev distance, 8-connected).
//
// Consumed by LightAmbientFill.shader (sky light contribution) and
// LightSun.shader (sun fades underground). Both sample _SkyExposureTex.
public class SkyExposure : MonoBehaviour {
    public static SkyExposure instance { get; private set; }

    World world;
    Texture2D exposureTex; // R8, nx x ny — sky light falloff (255 at sky, fades with distance)
    bool dirty;

    // Falloff distance read from LightFeature.penetrationDepth (unified with the
    // sub-tile edge-depth baked into tile normal maps by TileSpriteCache).

    // Called by WorldController/SaveSystem after tiles are ready.
    // The SkyExposure GameObject must exist in the scene (under Lighting).
    public static void InitializeWorld(World world) {
        if (instance == null) {
            Debug.LogError("SkyExposure: no instance in scene. Add a SkyExposure component to a GameObject under Lighting.");
            return;
        }
        instance.Initialize(world);
    }

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
    }

    void Initialize(World world) {
        if (this.world != null) {
            for (int x = 0; x < this.world.nx; x++)
                for (int y = 0; y < this.world.ny; y++)
                    this.world.GetTileAt(x, y).UnregisterCbBackgroundChanged(OnBackgroundChanged);
        }
        if (exposureTex != null) Destroy(exposureTex);

        this.world = world;
        int nx = world.nx;
        int ny = world.ny;

        exposureTex = new Texture2D(nx, ny, TextureFormat.R8, false);
        exposureTex.filterMode = FilterMode.Bilinear;
        exposureTex.wrapMode = TextureWrapMode.Clamp;

        RebuildExposureTexture();

        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
                world.GetTileAt(x, y).RegisterCbBackgroundChanged(OnBackgroundChanged);
    }

    void OnBackgroundChanged(Tile t) {
        dirty = true;
    }

    void LateUpdate() {
        if (dirty) {
            dirty = false;
            RebuildExposureTexture();
        }
    }

    // ── Sky light texture ─────────────────────────────────────────────────────
    // Sky light is emitted from sky-exposed tiles (!hasBackground) and falls
    // off with Chebyshev distance into surrounding material. Multi-source BFS
    // flood fill computes distance; smoothstep maps it to 0–255 falloff.
    // Falloff depth = LightFeature.penetrationDepth (shared with the sub-tile
    // edge-depth baked into tile normal maps by TileSpriteCache).
    void RebuildExposureTexture() {
        if (world == null) return;
        int nx = world.nx;
        int ny = world.ny;

        // BFS flood fill from all sky tiles (Chebyshev distance, 8-connected).
        float[] dist = new float[nx * ny];
        for (int i = 0; i < dist.Length; i++) dist[i] = float.MaxValue;

        var queue = new Queue<int>();
        for (int y = 0; y < ny; y++) {
            for (int x = 0; x < nx; x++) {
                if (!world.GetTileAt(x, y).hasBackground) {
                    int idx = y * nx + x;
                    dist[idx] = 0f;
                    queue.Enqueue(idx);
                }
            }
        }

        while (queue.Count > 0) {
            int idx = queue.Dequeue();
            int cx = idx % nx;
            int cy = idx / nx;
            float nd = dist[idx] + 1f;
            if (nd > LightFeature.penetrationDepth + 1f) continue;

            for (int dy = -1; dy <= 1; dy++) {
                int ny2 = cy + dy;
                if (ny2 < 0 || ny2 >= ny) continue;
                for (int dx = -1; dx <= 1; dx++) {
                    if (dx == 0 && dy == 0) continue;
                    int nx2 = cx + dx;
                    if (nx2 < 0 || nx2 >= nx) continue;
                    int nIdx = ny2 * nx + nx2;
                    if (nd < dist[nIdx]) {
                        dist[nIdx] = nd;
                        queue.Enqueue(nIdx);
                    }
                }
            }
        }

        // Smoothstep falloff: 1.0 at source, 0.0 one tile past penetrationDepth.
        // +1 offset so penetrationDepth=1 means "1 tile of reach beyond source"
        // rather than "fade to zero at the first neighbor."
        float falloffRange = LightFeature.penetrationDepth + 1f;
        byte[] exposureBytes = new byte[nx * ny];
        for (int i = 0; i < dist.Length; i++) {
            float t = Mathf.Clamp01(1f - dist[i] / falloffRange);
            float s = t * t * (3f - 2f * t); // smoothstep
            exposureBytes[i] = (byte)(s * 255f);
        }

        exposureTex.LoadRawTextureData(exposureBytes);
        exposureTex.Apply();

        Shader.SetGlobalTexture("_SkyExposureTex", exposureTex);
        Shader.SetGlobalVector("_GridSize", new Vector4(nx, ny, 0, 0));
    }

    void OnDestroy() {
        if (exposureTex != null) Destroy(exposureTex);
    }
}
