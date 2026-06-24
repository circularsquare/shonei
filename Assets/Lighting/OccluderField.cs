using UnityEngine;

// Occluder distance field for soft point-light wall shadows. A per-tile RFloat texture
// (_OccluderDist, bilinear) holding, for each cell, the (chamfer ≈ Euclidean) distance in
// tiles to the nearest occluder tile — 0 inside walls, growing outward, clamped to MaxDistTiles.
//
// LightCircle.shader sphere-traces this field from each lit pixel toward the light (Inigo
// Quilez soft shadows): it leaps by the distance-to-nearest-wall and softens by how closely the
// ray passes occluders relative to distance. This gives a smooth, view-INDEPENDENT penumbra
// (no banding, no perpendicular-source artifacts) and is cheap (a few big leaps in open space).
// Gated by SettingsManager.wallShadows via the _PointShadows global (set in LightFeature);
// lighting itself stays full per-pixel.
//
// Occluder = tile.type.solid && !bodyDrawnByStructure: real earth/walls, but NOT a burrow/pit's
// own carved footprint (which stays model-solid yet must not shadow its own interior — pairs
// with the Interior-tier promotion in NormalsCapture). Occluders are tiles only — free-standing
// buildings (houses) don't cast point-light shadows; terrain walls and burrows do. Rebuilt on a
// dirty flag when terrain solidity (cbTileTypeChanged) or a preservesTile footprint
// (cbBodyChanged) changes. Lifecycle mirrors SkyExposure, which this is modelled on.
public class OccluderField : MonoBehaviour {
    public static OccluderField instance { get; private set; }

    // Max distance the field encodes, in tiles. Beyond a few tiles the soft-shadow ratio
    // saturates (fully lit), so this only needs to cover the near-wall falloff; clamping keeps
    // the values bounded. The shader reads distances directly in tiles.
    const float MaxDistTiles = 8f;

    World world;
    Texture2D distTex; // RFloat, nx x ny — chamfer distance (tiles) to nearest occluder, bilinear
    bool dirty;

    // Called by WorldController/SaveSystem after tiles are ready (alongside SkyExposure).
    public static void InitializeWorld(World world) {
        if (instance == null) {
            Debug.LogError("OccluderField: no instance in scene. Add an OccluderField component to a GameObject under Lighting.");
            return;
        }
        instance.Initialize(world);
    }

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        // Fail-open before the first real field exists (load frames, or a scene without tiles):
        // whiteTexture reads ~1 tile of clearance everywhere, so the sphere-trace stays mostly
        // lit rather than fully shadowed. Replaced by RebuildField on Initialize.
        Shader.SetGlobalTexture("_OccluderDist", Texture2D.whiteTexture);
    }

    void Initialize(World world) {
        if (this.world != null) {
            for (int x = 0; x < this.world.nx; x++)
                for (int y = 0; y < this.world.ny; y++) {
                    Tile t = this.world.GetTileAt(x, y);
                    t.UnregisterCbTileTypeChanged(OnTileChanged);
                    t.UnregisterCbBodyChanged(OnTileChanged);
                }
        }
        if (distTex != null) Destroy(distTex);

        this.world = world;
        int nx = world.nx, ny = world.ny;

        distTex = new Texture2D(nx, ny, TextureFormat.RFloat, false);
        distTex.filterMode = FilterMode.Bilinear;
        distTex.wrapMode   = TextureWrapMode.Clamp;

        RebuildField();

        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                t.RegisterCbTileTypeChanged(OnTileChanged); // terrain mined / placed → solidity
                t.RegisterCbBodyChanged(OnTileChanged);     // preservesTile footprint carved / restored
            }
    }

    void OnTileChanged(Tile t) { dirty = true; }

    void LateUpdate() {
        if (dirty) {
            dirty = false;
            RebuildField();
        }
    }

    // Two-pass chamfer distance transform (forward + backward sweep, orthogonal cost 1,
    // diagonal √2) — a cheap close approximation of the Euclidean distance to the nearest
    // occluder cell. Occluders seed at 0; everything else floods outward. Clamped to
    // MaxDistTiles and stored in RFloat (tiles).
    void RebuildField() {
        if (world == null) return;
        int nx = world.nx, ny = world.ny;
        const float INF = 1e9f, D1 = 1f, D2 = 1.41421356f;

        float[] d = new float[nx * ny];
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++) {
                Tile t = world.GetTileAt(x, y);
                d[y * nx + x] = (t.type.solid && !t.bodyDrawnByStructure) ? 0f : INF;
            }

        // Forward sweep: relax from already-visited neighbours (W, NW, N, NE).
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++) {
                int i = y * nx + x;
                float v = d[i];
                if (x > 0)               v = Mathf.Min(v, d[i - 1] + D1);
                if (y > 0) {
                    v = Mathf.Min(v, d[i - nx] + D1);
                    if (x > 0)        v = Mathf.Min(v, d[i - nx - 1] + D2);
                    if (x < nx - 1)   v = Mathf.Min(v, d[i - nx + 1] + D2);
                }
                d[i] = v;
            }
        // Backward sweep: relax from the other neighbours (E, SE, S, SW).
        for (int y = ny - 1; y >= 0; y--)
            for (int x = nx - 1; x >= 0; x--) {
                int i = y * nx + x;
                float v = d[i];
                if (x < nx - 1)          v = Mathf.Min(v, d[i + 1] + D1);
                if (y < ny - 1) {
                    v = Mathf.Min(v, d[i + nx] + D1);
                    if (x < nx - 1)   v = Mathf.Min(v, d[i + nx + 1] + D2);
                    if (x > 0)        v = Mathf.Min(v, d[i + nx - 1] + D2);
                }
                d[i] = Mathf.Min(v, MaxDistTiles);
            }

        distTex.SetPixelData(d, 0);
        distTex.Apply();

        Shader.SetGlobalTexture("_OccluderDist", distTex);
        Shader.SetGlobalVector("_GridSize", new Vector4(nx, ny, 0, 0)); // shared with SkyExposure; same value
    }

    void OnDestroy() {
        if (distTex != null) Destroy(distTex);
    }
}
