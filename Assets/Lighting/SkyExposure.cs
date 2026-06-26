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
    int lastWallVersion = -1; // re-bake when WallField rebuilds (burrow walls drive the door routing)

    [Header("Open-passage sky light")]
    [Tooltip("How far (tiles) sky light travels down OPEN passages — caves, tunnels, burrow doors — " +
             "falling off with depth, so an opening admits a little light. 0 disables. Separate from " +
             "penetrationDepth (how far sky penetrates SOLID terrain, kept tight).")]
    [SerializeField] float openLightReach = 5f;
    [Tooltip("Overall strength of open-passage light (0-1). Lower = subtler seep into caves/burrows.")]
    [Range(0f, 1f)] [SerializeField] float openLightStrength = 1f;

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
                for (int y = 0; y < this.world.ny; y++) {
                    Tile t = this.world.GetTileAt(x, y);
                    t.UnregisterCbBackgroundChanged(OnDirty);
                    t.UnregisterCbBodyChanged(OnDirty);
                    t.UnregisterCbStructChanged(OnDirty);
                }
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
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                t.RegisterCbBackgroundChanged(OnDirty); // sky sources (hasBackground)
                t.RegisterCbBodyChanged(OnDirty);       // burrow carve/restore → burrow interior cells
                t.RegisterCbStructChanged(OnDirty);     // burrow placed/removed
            }
    }

    void OnDirty(Tile t) {
        dirty = true;
    }

    void LateUpdate() {
        // Also re-bake when WallField changed (burrow walls moved) — its rebuild may run after ours in
        // the same frame, so a version watch (not just our own dirty flag) guarantees we catch it.
        if (dirty || WallField.version != lastWallVersion) {
            dirty = false;
            lastWallVersion = WallField.version;
            RebuildExposureTexture();
        }
    }

    // Does a sky-flood step from (ax,ay) to adjacent (bx,by) cross a BURROW shell edge? Orthogonal:
    // the shared edge. Diagonal: blocked unless one L-route is burrow-free (no leaking past a corner).
    static bool BurrowBlocks(WallField wf, int ax, int ay, int bx, int by) {
        if (wf == null) return false;
        int dx = bx - ax, dy = by - ay;
        if (dy == 0) return wf.VEdgeBurrow(Mathf.Max(ax, bx), ay); // horizontal
        if (dx == 0) return wf.HEdgeBurrow(ax, Mathf.Max(ay, by)); // vertical
        bool r1 = !BurrowEdge(wf, ax, ay, bx, ay) && !BurrowEdge(wf, bx, ay, bx, by);
        bool r2 = !BurrowEdge(wf, ax, ay, ax, by) && !BurrowEdge(wf, ax, by, bx, by);
        return !(r1 || r2);
    }

    static bool BurrowEdge(WallField wf, int ax, int ay, int bx, int by) {
        if (ay == by) return wf.VEdgeBurrow(Mathf.Max(ax, bx), ay);
        return wf.HEdgeBurrow(ax, Mathf.Max(ay, by));
    }

    // Pass B — sky light travelling down OPEN passages (open cells + burrow interiors), so a cave mouth
    // or a burrow door admits light that falls off with depth. This is what penetrationDepth (tuned
    // tight for solid terrain) can't express. Blocked by solid rock and burrow shells; reaches
    // openLightReach passable tiles from the sky. Combined with the solid-penetration pass via max(),
    // so terrain shading is unchanged — this only ADDS light in open underground space near openings.
    float[] FloodOpenPassages(int nx, int ny, WallField wf) {
        var dist = new float[nx * ny];
        for (int i = 0; i < dist.Length; i++) dist[i] = float.MaxValue;
        var q = new Queue<int>();
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++) {
                Tile t = world.GetTileAt(x, y);
                if (!t.hasBackground && !t.bodyDrawnByStructure) { int idx = y * nx + x; dist[idx] = 0f; q.Enqueue(idx); }
            }
        while (q.Count > 0) {
            int idx = q.Dequeue();
            int cx = idx % nx, cy = idx / nx;
            float nd = dist[idx] + 1f;
            if (nd > openLightReach + 1f) continue;
            for (int dy = -1; dy <= 1; dy++) {
                int ny2 = cy + dy; if (ny2 < 0 || ny2 >= ny) continue;
                for (int dx = -1; dx <= 1; dx++) {
                    if (dx == 0 && dy == 0) continue;
                    int nx2 = cx + dx; if (nx2 < 0 || nx2 >= nx) continue;
                    Tile nt = world.GetTileAt(nx2, ny2);
                    // Travel only through passable cells (open OR a carved burrow hollow); solid rock
                    // blocks — that's the penetration pass's job. Burrow shells block too (door only).
                    if (nt.type.solid && !nt.bodyDrawnByStructure) continue;
                    if (BurrowBlocks(wf, cx, cy, nx2, ny2)) continue;
                    int nIdx = ny2 * nx + nx2;
                    if (nd < dist[nIdx]) { dist[nIdx] = nd; q.Enqueue(nIdx); }
                }
            }
        }
        return dist;
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

        var wf = WallField.instance;

        var queue = new Queue<int>();
        for (int y = 0; y < ny; y++) {
            for (int x = 0; x < nx; x++) {
                Tile t = world.GetTileAt(x, y);
                // Sky source = a sky-open cell. A burrow interior is NEVER a source even where it lacks
                // a background wall (shallow carves): otherwise it reads as open sky and gets full sun.
                if (!t.hasBackground && !t.bodyDrawnByStructure) {
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
                    // A burrow is a hollow carved inside solid: sky can't cross its shell (roof/walls),
                    // only its door. Block stepping across a burrow wall edge so the flood reaches the
                    // interior only through the open door — "floodfillable from the entrance, not the
                    // roof". Ordinary terrain edges aren't blocked, so sky still penetrates solid for
                    // the surface→underground gradient.
                    if (BurrowBlocks(wf, cx, cy, nx2, ny2)) continue;
                    int nIdx = ny2 * nx + nx2;
                    if (nd < dist[nIdx]) {
                        dist[nIdx] = nd;
                        queue.Enqueue(nIdx);
                    }
                }
            }
        }

        // Pass B: open-passage light (down caves / tunnels / burrow doors). Combined below via max().
        float[] distB = openLightReach > 0f ? FloodOpenPassages(nx, ny, wf) : null;

        // Smoothstep falloff: 1.0 at source, 0.0 one tile past penetrationDepth.
        // +1 offset so penetrationDepth=1 means "1 tile of reach beyond source"
        // rather than "fade to zero at the first neighbor."
        float falloffRange = LightFeature.penetrationDepth + 1f;
        float openRange    = openLightReach + 1f;
        byte[] exposureBytes = new byte[nx * ny];
        for (int i = 0; i < dist.Length; i++) {
            float t = Mathf.Clamp01(1f - dist[i] / falloffRange);
            float s = t * t * (3f - 2f * t); // smoothstep (solid penetration)
            if (distB != null) {
                float tb = Mathf.Clamp01(1f - distB[i] / openRange);
                float sb = tb * tb * (3f - 2f * tb) * openLightStrength; // open-passage light
                s = Mathf.Max(s, sb);
            }
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
