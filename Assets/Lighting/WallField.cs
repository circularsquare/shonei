using UnityEngine;

// Per-edge light-wall field. Tracks, for every boundary BETWEEN two adjacent tiles, whether that
// edge is a wall that should block light — the precursor data for a future flood-fill ("propagated")
// point-lighting pass: flood-fill moves light cell→cell across edges, so "does this edge block?" is
// exactly the per-step cost it needs. Nothing consumes this yet — today it only holds the grid and
// drives an optional debug overlay so we can confirm the walls are tracked correctly.
//
// Two wall sources, OR'd together on rebuild:
//
//   SrcTerrain — the boundary between a solid tile and an open one (an edge with a solid occluder on
//     exactly one side). Occluder = tile.type.solid && !bodyDrawnByStructure, same set as
//     OccluderField. Stores cave/terrain outlines, not filled interiors. Buildings do NOT occlude
//     (shallow front-lit facades), so they contribute nothing here.
//
//   SrcBurrow — a BURROW's footprint perimeter. A burrow cell is a SOLID tile with a hollow carved
//     in it (preservesTile → bodyDrawnByStructure): the tile material forms walls around the carved
//     pocket. So every footprint-perimeter edge is a wall — INCLUDING a ceiling with open air above
//     it — regardless of the neighbouring tile, because the wall is the burrow's own solid shell, not
//     the neighbour. This is the one thing per-edge walls express that per-cell solidity can't:
//     without it the whole field would be redundant with OccluderField. Interior edges (between two
//     cells of the same burrow) stay open so the pocket is one connected, lightable space; the door
//     edge (flagged in the door tile's bodyEdgeSuppressMask) stays open so light enters through it.
//
// Representation: two edge grids of source bytes.
//   - vEdge[lineX, y]  — VERTICAL edge between tiles (lineX-1, y) and (lineX, y). lineX ∈ [0, nx].
//   - hEdge[x, lineY]  — HORIZONTAL edge between tiles (x, lineY-1) and (x, lineY). lineY ∈ [0, ny].
//
// Lifecycle mirrors OccluderField / SkyExposure: a scene-placed singleton, InitializeWorld after
// tiles are ready, rebuilt on a dirty flag in LateUpdate when terrain solidity (cbTileTypeChanged),
// a preservesTile footprint (cbBodyChanged), or a structure (cbStructChanged) changes.
//
// CONSUMED BY LIGHTING: the SrcBurrow edges + burrow-interior cells are baked into `_WallBurrowTex`
// (an (nx+1)×(ny+1) point-sampled mask):
//   R = vertical burrow wall on grid line x at row y;
//   G = horizontal burrow wall on column x at grid line y;
//   B = 1 if cell (x,y) is a burrow interior cell (bodyDrawnByStructure).
// LightCircle.shader (point lights) and LightSun.shader (the sun) ray-march toward the light: while the
// ray is inside a burrow (B) they accumulate its chord length, and when it crosses a burrow wall (R/G)
// that chord is committed as shell thickness → a SOFT shadow (grazing rays cut a shorter chord). A ray
// that reaches the light still inside (an interior torch) or leaves through the open door (no wall
// crossing) is not shadowed — so a burrow is sealed from outside light/sun except through its door,
// while an inside torch still lights it. (Terrain occlusion stays on OccluderField, unchanged.
// SkyExposure separately keeps burrows dark by not flooding sky INTO bodyDrawnByStructure cells — it
// reads the tile flag directly, not this texture.)
public class WallField : MonoBehaviour {
    public static WallField instance { get; private set; }

    public const byte SrcTerrain = 1;
    public const byte SrcBurrow  = 2;

    // Per-side bits matching Tile.bodyEdgeSuppressMask (the door side lives in this layout).
    const byte SideL = 1, SideR = 2, SideD = 4, SideU = 8;

    // Chunk-mesh tiles are centred on integer coords (the terrain mesh GO sits at (-0.5,-0.5)), so
    // tile (x,y) spans world [x-0.5, x+0.5] and edges fall on half-integer coordinates.
    const float CellOffset = -0.5f;

    [Tooltip("Draw tracked wall edges as gizmo lines (Scene view, or Game view with Gizmos on). " +
             "Terrain walls cyan, burrow-perimeter walls magenta.")]
    public bool debugDraw;

    World world;
    int nx, ny;
    byte[] vEdge; // (nx+1) * ny — index: lineX * ny + y
    byte[] hEdge; // nx * (ny+1) — index: x * (ny+1) + lineY
    bool dirty;

    // Burrow-wall mask for the point-light ray-march (see class header). (nx+1)×(ny+1), point-sampled.
    Texture2D burrowTex;
    Color32[] burrowPx;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    // Called by WorldController / SaveSystem after tiles are ready (alongside OccluderField).
    public static void InitializeWorld(World world) {
        if (instance == null) {
            Debug.LogError("WallField: no instance in scene. Add a WallField component to a GameObject under Lighting.");
            return;
        }
        instance.Initialize(world);
    }

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        // Fail-safe default before the first real bake (load frames / scene without tiles): black =
        // no burrow walls anywhere, so LightCircle's ray-march blocks nothing.
        Shader.SetGlobalTexture("_WallBurrowTex", Texture2D.blackTexture);
        Shader.SetGlobalVector("_WallTexSize", new Vector4(1, 1, 0, 0));
    }

    void Initialize(World world) {
        if (this.world != null) {
            for (int x = 0; x < this.world.nx; x++)
                for (int y = 0; y < this.world.ny; y++) {
                    Tile t = this.world.GetTileAt(x, y);
                    t.UnregisterCbTileTypeChanged(OnTileChanged);
                    t.UnregisterCbBodyChanged(OnTileChanged);
                    t.UnregisterCbStructChanged(OnTileChanged);
                }
        }

        this.world = world;
        nx = world.nx;
        ny = world.ny;
        vEdge = new byte[(nx + 1) * ny];
        hEdge = new byte[nx * (ny + 1)];

        if (burrowTex != null) Destroy(burrowTex);
        burrowTex = new Texture2D(nx + 1, ny + 1, TextureFormat.RGBA32, false);
        // Bilinear so LightCircle can read the wall mask BLENDED ALONG each wall → soft (fuzzy)
        // diagonal shadow boundaries at corners/door jambs. The shader pins the perpendicular axis to
        // the texel centre when sampling, so walls don't smear sideways; centre-sampling consumers
        // (LightSun, the interior-B test) read exact values, so bilinear is a no-op for them.
        burrowTex.filterMode = FilterMode.Bilinear;
        burrowTex.wrapMode   = TextureWrapMode.Clamp;
        burrowPx = new Color32[(nx + 1) * (ny + 1)];

        RebuildField();

        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                t.RegisterCbTileTypeChanged(OnTileChanged); // terrain mined / placed → solidity
                t.RegisterCbBodyChanged(OnTileChanged);     // preservesTile footprint carved / restored
                t.RegisterCbStructChanged(OnTileChanged);   // burrow placed / removed → perimeter walls
            }
    }

    void OnTileChanged(Tile t) { dirty = true; }

    void LateUpdate() {
        if (dirty) {
            dirty = false;
            RebuildField();
        }
        if (debugDraw) DrawDebug();
    }

    // ── Accessors (for the future flood-fill) ──────────────────────────────

    // Vertical edge between tiles (lineX-1, y) and (lineX, y). lineX ∈ [0, nx].
    public bool VEdge(int lineX, int y) {
        if (vEdge == null || lineX < 0 || lineX > nx || y < 0 || y >= ny) return false;
        return vEdge[lineX * ny + y] != 0;
    }

    // Horizontal edge between tiles (x, lineY-1) and (x, lineY). lineY ∈ [0, ny].
    public bool HEdge(int x, int lineY) {
        if (hEdge == null || x < 0 || x >= nx || lineY < 0 || lineY > ny) return false;
        return hEdge[x * (ny + 1) + lineY] != 0;
    }

    // ── Rebuild ────────────────────────────────────────────────────────────

    // Tiles outside the grid count as open, so an occluder on the world border still walls its outer
    // edge.
    bool Occludes(int x, int y) {
        if (x < 0 || x >= nx || y < 0 || y >= ny) return false;
        Tile t = world.GetTileAt(x, y);
        return t != null && t.type.solid && !t.bodyDrawnByStructure;
    }

    // True if (x,y) belongs to the same burrow footprint as b — used to keep interior edges open.
    bool SameBurrow(int x, int y, Building b) {
        if (x < 0 || x >= nx || y < 0 || y >= ny) return false;
        Tile t = world.GetTileAt(x, y);
        return t != null && t.bodyDrawnByStructure && t.building == b;
    }

    void RebuildField() {
        if (world == null) return;
        System.Array.Clear(vEdge, 0, vEdge.Length);
        System.Array.Clear(hEdge, 0, hEdge.Length);

        // Terrain pass: a wall is an occluder/open boundary (exactly one side solid). Every edge once.
        for (int y = 0; y < ny; y++)
            for (int lineX = 0; lineX <= nx; lineX++)
                if (Occludes(lineX - 1, y) != Occludes(lineX, y))
                    vEdge[lineX * ny + y] |= SrcTerrain;
        for (int x = 0; x < nx; x++)
            for (int lineY = 0; lineY <= ny; lineY++)
                if (Occludes(x, lineY - 1) != Occludes(x, lineY))
                    hEdge[x * (ny + 1) + lineY] |= SrcTerrain;

        // Burrow pass: a carved-in-solid cell walls its footprint perimeter (any side whose neighbour
        // isn't part of the same burrow), minus the door — independent of neighbour solidity.
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++) {
                Tile t = world.GetTileAt(x, y);
                if (t == null || !t.bodyDrawnByStructure) continue;
                Building b = t.building;
                if (b == null) continue;
                byte door = b.structType.preservesTile ? (byte)(t.bodyEdgeSuppressMask & 0xF) : (byte)0;

                if ((door & SideL) == 0 && !SameBurrow(x - 1, y, b)) vEdge[x * ny + y]               |= SrcBurrow;
                if ((door & SideR) == 0 && !SameBurrow(x + 1, y, b)) vEdge[(x + 1) * ny + y]         |= SrcBurrow;
                if ((door & SideD) == 0 && !SameBurrow(x, y - 1, b)) hEdge[x * (ny + 1) + y]         |= SrcBurrow;
                if ((door & SideU) == 0 && !SameBurrow(x, y + 1, b)) hEdge[x * (ny + 1) + y + 1]     |= SrcBurrow;
            }

        BakeBurrowTexture();
    }

    // Pack the SrcBurrow edges + burrow-interior cells into _WallBurrowTex for the lighting ray-march.
    // Texel (x,y) in an (nx+1)×(ny+1) grid: R = vertical burrow wall on grid line x at row y
    // (vEdge[x,y]); G = horizontal burrow wall on column x at grid line y (hEdge[x,y]); B = 1 if cell
    // (x,y) is a burrow interior (bodyDrawnByStructure). Row 0 = world y 0 (Unity uv origin
    // bottom-left), matching the world-up convention the shaders sample with.
    void BakeBurrowTexture() {
        int W = nx + 1;
        System.Array.Clear(burrowPx, 0, burrowPx.Length);
        for (int y = 0; y < ny; y++)
            for (int lineX = 0; lineX <= nx; lineX++)
                if ((vEdge[lineX * ny + y] & SrcBurrow) != 0) burrowPx[y * W + lineX].r = 255;
        for (int x = 0; x < nx; x++)
            for (int lineY = 0; lineY <= ny; lineY++)
                if ((hEdge[x * (ny + 1) + lineY] & SrcBurrow) != 0) burrowPx[lineY * W + x].g = 255;
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++) {
                Tile t = world.GetTileAt(x, y);
                if (t != null && t.bodyDrawnByStructure) burrowPx[y * W + x].b = 255;
            }

        burrowTex.SetPixels32(burrowPx);
        burrowTex.Apply();
        Shader.SetGlobalTexture("_WallBurrowTex", burrowTex);
        Shader.SetGlobalVector("_WallTexSize", new Vector4(W, ny + 1, 0, 0));
    }

    // ── Debug overlay ──────────────────────────────────────────────────────

    // Terrain walls cyan, burrow-perimeter walls magenta (so the burrow ceilings/walls — the bit that
    // isn't derivable from neighbour solidity — stand out). Drawn fresh each frame (DrawLine 1-frame).
    void DrawDebug() {
        if (vEdge == null) return;
        for (int y = 0; y < ny; y++)
            for (int lineX = 0; lineX <= nx; lineX++) {
                byte s = vEdge[lineX * ny + y];
                if (s == 0) continue;
                float wx = lineX + CellOffset;
                Debug.DrawLine(new Vector3(wx, y + CellOffset, 0f), new Vector3(wx, y + 1 + CellOffset, 0f), SrcColor(s));
            }
        for (int x = 0; x < nx; x++)
            for (int lineY = 0; lineY <= ny; lineY++) {
                byte s = hEdge[x * (ny + 1) + lineY];
                if (s == 0) continue;
                float wy = lineY + CellOffset;
                Debug.DrawLine(new Vector3(x + CellOffset, wy, 0f), new Vector3(x + 1 + CellOffset, wy, 0f), SrcColor(s));
            }
    }

    static Color SrcColor(byte s) {
        return (s & SrcBurrow) != 0 ? Color.magenta : Color.cyan;
    }

    void OnDestroy() {
        if (burrowTex != null) Destroy(burrowTex);
        if (instance == this) instance = null;
    }
}
