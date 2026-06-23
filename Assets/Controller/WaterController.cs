using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Simulates and renders a cellular-automaton water fluid system.
// Water is stored as a ushort (0–160, fixed-point) on each Tile. Each TickUpdate:
//   1. SimulateStep — water falls downward then spreads laterally (volume-preserving).
//   2. UpdateSurfaceMask — builds a 1-byte-per-pixel surface mask and uploads it to the GPU.
//
// GPU rendering (Assets/Lighting/Water.shader) is deliberately simple: one texture sample
// per pixel, then either discard / surface color / shimmer lerp. All neighbour-air detection
// happens on the CPU every 0.2 s instead of in the shader every frame, which is much cheaper
// on slow/integrated GPUs due to lower texture bandwidth and no branch divergence.
//
// Surface mask encoding (TextureFormat.R8, one byte per game pixel):
//   0   = transparent  (no water)
//   127 = interior water
//   255 = surface water (at least one of 8 neighbours is open air)
//
// Called every 0.2 seconds from World.Update (same cadence as inventory tick).
public class WaterController : MonoBehaviour {
    public static WaterController instance { get; private set; }

    [Header("Water Colors")]
    [SerializeField] Color waterColorDark  = new Color(0.08f, 0.35f, 0.85f, 0.55f);
    [SerializeField] Color waterColorLight = new Color(0.18f, 0.50f, 0.95f, 0.50f);
    [SerializeField] Color surfaceColor    = new Color(1.00f, 1.00f, 1.00f, 0.75f);

    // Inspector-assigned so the shader is force-included in builds (see LightFeature
    // for rationale — Shader.Find() works in editor but doesn't survive build stripping).
    [SerializeField] Shader waterShader;
    // Solid 0.9-alpha base layer that sits behind the background wall. Occludes the
    // parallax sky painting while letting the cave wall draw on top so cave pools
    // keep their see-through look. See SPEC-rendering.md §Water Rendering.
    [SerializeField] Shader waterUnderlayShader;

    // Asset pixels per tile edge. 16 PPU assets → 16 game pixels per tile edge.
    private const int PixelsPerTile = 16;

    // Surface mask: R8 texture, one byte per game pixel.
    private Texture2D _surfaceTex;
    private byte[]    _surfaceBytes;  // reused buffer — no per-tick allocation
    private int       _texW, _texH;

    // Per-tile tint: RGBA32, one texel per world tile (nx × ny). Decorative zones stamp
    // their stored liquid's liquidColor here each tick; Point-filtered so adjacent tiles
    // with different tints don't bleed across tile borders. Alpha=0 → shader falls back
    // to its global _WaterColorDark/Light (natural water, fountains, any untinted tile).
    private Texture2D _tintTex;
    private byte[]    _tintBytes;

    // Per-tile caches rebuilt at the start of each UpdateSurfaceMask.
    private int[]  _waterPixelHeights; // game pixel rows filled per tile (0–PixelsPerTile)
    private bool[] _tileIsSolid;       // true if tile.type.solid

    private Material _waterMat;
    private Material _underlayMat;
    // Water-sprite GameObjects — kept as refs so a world re-allocation can destroy
    // the old ones before creating new ones at the new world scale.
    private GameObject _waterSpriteGo;
    private GameObject _underlaySpriteGo;

    // Decorative-zone liquid (tanks, fountains, brewery) renders on its OWN sprite, separate
    // from natural/sim water, so the two can sort differently: natural water sorts in FRONT of
    // mice (submersion tint), but container liquid sorts BEHIND mice (a mouse in front of a tank
    // occludes it) while still in front of the building's painted backdrop. Shares _tintTex.
    private Texture2D  _decorTex;
    private byte[]     _decorBytes;
    private Material   _decorMat;
    private GameObject _decorSpriteGo;

    // Sort orders (front→back): tiles 78..74 > natural water > mice 48..64 > decorative water
    // > buildings ≤17. All water pinned to one lighting bucket so the raised draw orders don't
    // change water's flat NormalsCaptureWater shading. See SPEC-rendering.md §Water Rendering.
    const int WaterSortingOrder         = 72;
    const int DecorWaterSortingOrder    = 20;
    const int WaterUnderlaySortingOrder = -15;
    const int WaterLightingBucket       = 0;

    // True once the size-dependent resources have been built. Used to gate the
    // OnWorldAllocated handler so we don't double-build on the very first
    // allocation (Start handles that path); the handler only fires on
    // subsequent reallocations after Start has completed.
    private bool _worldResourcesBuilt;

    // Structures with a `{stem}_w.png` companion sprite register their water zone here,
    // keyed by Structure. Overlaid into _surfaceBytes each UpdateSurfaceMask tick.
    // Two render modes are supported per zone:
    //   - Binary (fountains etc.): reservoir.HasFuel() gates all-or-nothing.
    //   - Fill-level (tanks, structType.isLiquidStorage): only the bottom portion renders,
    //     scaled to the fraction of water currently in storage. localYs + localMinY/Max
    //     enable the per-pixel height test without re-scanning the sprite every tick.
    private struct DecorativeZone {
        public List<Vector2Int> worldPixels; // already mirrored + world-shifted
        public List<int>        localYs;     // parallel to worldPixels — local Y in sprite coords
        public int              localMinY, localMaxY;
    }
    private readonly Dictionary<Structure, DecorativeZone> _decorativeZones
        = new Dictionary<Structure, DecorativeZone>();

    // Internal fixed-point scale: 10 internal units = 1 display unit (tile fully filled at 160).
    // Scaling up from 16 eliminates the integer-truncation dead zone in the spread formula
    // (diff/2 == 0 when diff == 1), which would otherwise leave water in a staircase pattern.
    // The dead zone shrinks to 1/10 of a visual unit — sub-pixel.
    public const ushort WaterMax = 160;

    // Tiles with water in [1, ResidualBandMax) render as 0 pixel rows (round(w/160*16) = 0
    // for w ≤ 4 and = 1 for w = 5; we use 5 here as the strict upper bound). Pass 4 jitters
    // tiles in this band so abandoned residual eventually drains off an open edge rather
    // than getting re-grown into a visible pool by RainReplenish.
    public const ushort ResidualBandMax = 5;

    // Alternates each SimulateStep to prevent directional spread bias.
    private bool flipDir = false;

    void Awake() {
        if (instance != null) {
            Debug.LogError("WaterController: duplicate instance detected");
            return;
        }
        instance = this;
        // Re-build the world-sized resources whenever the world grid is (re-)allocated.
        // First-time build is driven by Start (we need the frame wait); subsequent
        // re-allocations come through this handler.
        World.OnWorldAllocated += HandleWorldReallocated;
    }

    void OnDestroy() {
        World.OnWorldAllocated -= HandleWorldReallocated;
    }

    void HandleWorldReallocated() {
        // Skip the first allocation — Start() drives it. After Start runs once,
        // subsequent OnWorldAllocated events come from SaveSystem.LoadFromJson
        // when the saved world size differs from the current size, and we tear
        // down + rebuild at the new dimensions.
        if (!_worldResourcesBuilt) return;
        DisposeWorldSizedResources();
        BuildWorldSizedResources();
        UpdateSurfaceMask();
    }

    IEnumerator Start() {
        // Wait one frame so WorldController.Start() has created all tile references.
        yield return null;
        BuildWorldSizedResources();
        // Skip mask update if the build aborted (e.g. missing shader). The
        // shader-null branch in BuildWorldSizedResources logs an error and
        // returns without setting _worldResourcesBuilt; calling UpdateSurfaceMask
        // here would dereference a null material.
        if (_worldResourcesBuilt) UpdateSurfaceMask();
    }

    // Allocates the buffers, textures, materials, and sprite GameObjects sized to
    // the current world. Idempotent only via DisposeWorldSizedResources first —
    // call dispose then build to resize cleanly.
    void BuildWorldSizedResources() {
        World world = World.instance;

        _texW = world.nx * PixelsPerTile;
        _texH = world.ny * PixelsPerTile;
        _waterPixelHeights = new int[world.nx * world.ny];
        _tileIsSolid       = new bool[world.nx * world.ny];

        // R8 surface mask: 1 byte per game pixel — 1.25 MB for a 100×50 tile world.
        _surfaceTex   = new Texture2D(_texW, _texH, TextureFormat.R8, false);
        _surfaceTex.filterMode = FilterMode.Point;
        _surfaceBytes = new byte[_texW * _texH];

        // Decorative-zone mask — same dims, rendered by its own sprite/material (see field comment).
        _decorTex   = new Texture2D(_texW, _texH, TextureFormat.R8, false);
        _decorTex.filterMode = FilterMode.Point;
        _decorBytes = new byte[_texW * _texH];

        // RGBA32 per-tile tint: 20 KB for a 100×50 world. Point filter is critical —
        // bilinear would bleed colors across tile borders.
        _tintTex = new Texture2D(world.nx, world.ny, TextureFormat.RGBA32, false);
        _tintTex.filterMode = FilterMode.Point;
        _tintBytes = new byte[world.nx * world.ny * 4];

        // Create material and push colours.
        if (waterShader == null) {
            Debug.LogError("WaterController: waterShader unassigned in Inspector — assign Water/WaterSurface (Assets/Lighting/Water.shader)");
            return;
        }
        _waterMat = new Material(waterShader);
        _waterMat.SetColor("_WaterColorDark",  waterColorDark);
        _waterMat.SetColor("_WaterColorLight", waterColorLight);
        _waterMat.SetColor("_SurfaceColor",    surfaceColor);
        _waterMat.SetVector("_WorldPixelSize", new Vector4(_texW, _texH, 0, 0));
        _waterMat.SetTexture("_SurfaceTex", _surfaceTex);
        _waterMat.SetTexture("_TintTex",    _tintTex);

        // Decorative-zone water material — same shader/colours as the front water, but bound to
        // _decorTex and drawn by its own sprite at a lower sort order (behind mice, in front of
        // buildings). Shares _tintTex so per-liquid tints flow to it too.
        _decorMat = new Material(waterShader);
        _decorMat.SetColor("_WaterColorDark",  waterColorDark);
        _decorMat.SetColor("_WaterColorLight", waterColorLight);
        _decorMat.SetColor("_SurfaceColor",    surfaceColor);
        _decorMat.SetVector("_WorldPixelSize", new Vector4(_texW, _texH, 0, 0));
        _decorMat.SetTexture("_SurfaceTex", _decorTex);
        _decorMat.SetTexture("_TintTex",    _tintTex);

        if (waterUnderlayShader == null) {
            Debug.LogError("WaterController: waterUnderlayShader unassigned in Inspector — assign Water/WaterUnderlay (Assets/Lighting/WaterUnderlay.shader)");
            return;
        }
        // Reuses the same _surfaceTex and _tintTex by reference, so per-tick
        // texture updates flow to both materials without re-binding.
        _underlayMat = new Material(waterUnderlayShader);
        _underlayMat.SetColor("_BaseColor",
            new Color(waterColorDark.r, waterColorDark.g, waterColorDark.b, 0.9f));
        _underlayMat.SetTexture("_SurfaceTex", _surfaceTex);
        _underlayMat.SetTexture("_TintTex",    _tintTex);

        // World-spanning sprite: 1×1 white pixel at PPU=1, scaled to (nx, ny) Unity units.
        // UV spans 0–1 across the world, which the shader maps to game-pixel coordinates.
        // Placed at (−0.5, −0.5) to align with the tile grid (tile centres are at integers).
        Texture2D whiteTex = new Texture2D(1, 1);
        whiteTex.filterMode = FilterMode.Point;
        whiteTex.SetPixel(0, 0, Color.white);
        whiteTex.Apply();
        Sprite waterSprite = Sprite.Create(whiteTex, new Rect(0, 0, 1, 1), Vector2.zero, 1f);

        _waterSpriteGo = new GameObject("WaterSprite");
        _waterSpriteGo.transform.position   = new Vector3(-0.5f, -0.5f, 0f);
        _waterSpriteGo.transform.localScale = new Vector3(world.nx, world.ny, 1f);

        int waterLayer = LayerMask.NameToLayer("Water");
        if (waterLayer < 0)
            Debug.LogError("WaterController: 'Water' layer not found — create it in Edit → Project Settings → Tags and Layers");
        else
            _waterSpriteGo.layer = waterLayer;

        SpriteRenderer sr = _waterSpriteGo.AddComponent<SpriteRenderer>();
        sr.sprite      = waterSprite;
        sr.material    = _waterMat;
        // Natural/sim water sorts in FRONT of mice and buildings (submersion tint) but BEHIND
        // tile bodies (78..74), so the solid tile still hides the bleed pixels we write into
        // solid neighbours (see UpdateSurfaceMask) — the bleed only shows through the tile
        // sprite's transparent bevel gaps. Still in front of the background wall (-10) so cave
        // water reads against dirt. Container liquids (tanks/fountains) are NOT here — they're
        // on _decorSpriteGo (behind mice). Bucket is pinned so the raised order doesn't change
        // water's flat NormalsCaptureWater shading.
        sr.sortingOrder = WaterSortingOrder;
        LightReceiverUtil.SetSortBucket(sr);
        sr.renderingLayerMask = 1u << WaterLightingBucket;

        // Underlay sprite — same geometry, same Water Unity layer, same shared
        // surface mask. Sorts at -15 (behind the background wall at -10) so the cave
        // wall draws on top, preserving the underground see-through. Above ground
        // (no background-wall pixels) the 0.9-alpha base occludes the parallax
        // sky painting that the front 0.5-alpha layer would otherwise let through.
        // No shimmer/sparkles — those come from the front Water.shader only,
        // avoiding the doubled-sparkle blowout that two stacked shimmer layers
        // would produce.
        _underlaySpriteGo = new GameObject("WaterSpriteUnderlay");
        _underlaySpriteGo.transform.position   = _waterSpriteGo.transform.position;
        _underlaySpriteGo.transform.localScale = _waterSpriteGo.transform.localScale;
        if (waterLayer >= 0) _underlaySpriteGo.layer = waterLayer;
        SpriteRenderer underSr = _underlaySpriteGo.AddComponent<SpriteRenderer>();
        underSr.sprite       = waterSprite; // share the 1×1 white sprite
        underSr.material     = _underlayMat;
        underSr.sortingOrder = WaterUnderlaySortingOrder;
        LightReceiverUtil.SetSortBucket(underSr);

        // Decorative-zone water sprite — same world-spanning geometry + Water layer as the front
        // sprite, but sorts at DecorWaterSortingOrder (in front of buildings ≤17, behind mice) and
        // reads _decorTex. No underlay: each zone's painted opaque backdrop is its own occluder.
        _decorSpriteGo = new GameObject("WaterSpriteDecor");
        _decorSpriteGo.transform.position   = _waterSpriteGo.transform.position;
        _decorSpriteGo.transform.localScale = _waterSpriteGo.transform.localScale;
        if (waterLayer >= 0) _decorSpriteGo.layer = waterLayer;
        SpriteRenderer decorSr = _decorSpriteGo.AddComponent<SpriteRenderer>();
        decorSr.sprite       = waterSprite;
        decorSr.material     = _decorMat;
        decorSr.sortingOrder = DecorWaterSortingOrder;
        LightReceiverUtil.SetSortBucket(decorSr);
        decorSr.renderingLayerMask = 1u << WaterLightingBucket;

        _worldResourcesBuilt = true;
    }

    // Destroys the world-sized resources so they can be rebuilt at a new size.
    // Unity-managed objects (Texture2D, Material, GameObject) require explicit
    // Destroy — letting GC handle them leaks GPU memory.
    void DisposeWorldSizedResources() {
        if (_surfaceTex != null)        { Destroy(_surfaceTex); _surfaceTex = null; }
        if (_decorTex != null)          { Destroy(_decorTex); _decorTex = null; }
        if (_tintTex != null)           { Destroy(_tintTex); _tintTex = null; }
        if (_waterMat != null)          { Destroy(_waterMat); _waterMat = null; }
        if (_decorMat != null)          { Destroy(_decorMat); _decorMat = null; }
        if (_underlayMat != null)       { Destroy(_underlayMat); _underlayMat = null; }
        if (_waterSpriteGo != null)     { Destroy(_waterSpriteGo); _waterSpriteGo = null; }
        if (_underlaySpriteGo != null)  { Destroy(_underlaySpriteGo); _underlaySpriteGo = null; }
        if (_decorSpriteGo != null)     { Destroy(_decorSpriteGo); _decorSpriteGo = null; }
        _surfaceBytes      = null;
        _decorBytes        = null;
        _tintBytes         = null;
        _waterPixelHeights = null;
        _tileIsSolid       = null;
        _worldResourcesBuilt = false;
    }

    // Called by World.Update every 0.2 seconds.
    public void TickUpdate() {
        SimulateStep();
        UpdateSurfaceMask();
    }

    // Runs SimulateStep up to maxSteps times, exiting early when the step's
    // total transfer count drops to moveThreshold or below. Refreshes the
    // surface mask once at the end. Used by WorldController.GenerateDefault
    // so a freshly-generated world's water is settled before the player sees
    // it — uniform-level gen-time placement still leaks across worm tunnels
    // and pool-carve seams that the live CA would otherwise need a few real
    // ticks to drain.
    //
    // moveThreshold = 0 disables early exit. moveThreshold > 0 stops on the
    // first step whose total moved water units is at or below the threshold;
    // staircase artefacts that pass 3 chips away one unit per step show up
    // as small persistent moves, so anything in single digits is "settled".
    public void Settle(int maxSteps, int moveThreshold) {
        for (int i = 0; i < maxSteps; i++) {
            int moves = SimulateStep();
            if (moves <= moveThreshold) break;
        }
        UpdateSurfaceMask();
    }

    // One cellular-automaton step. Returns the total water units transferred
    // across all four passes — used by Settle() to detect convergence; live
    // TickUpdate calls discard the return.
    //
    // Pass 1 (bottom-to-top): pour water straight down into the tile below.
    // Pass 2 (bottom-to-top, alternating L/R): equalize with one horizontal neighbor (diff/2).
    // Pass 3 (same direction as Pass 2): look-ahead equalization for diff-1 slopes.
    //   When a tile is exactly 1 unit below its neighbor, scan further in that direction
    //   for a tile at +2 or higher (ignoring plateau tiles at +1). If found, pull 1 unit
    //   from that elevated source to the low tile, flattening the slope step by step.
    //   This prevents CA water from getting permanently stuck in a visible staircase.
    // Pass 4 (random direction per tile): look-ahead drain for sub-pixel residual,
    //   the dual of Pass 3. For each residual tile (water < ResidualBandMax) scan
    //   along a randomly-chosen direction across plateau tiles at equal water and
    //   push 1 unit to the first strictly-lower tile found, mirroring Pass 3's
    //   plateau-walking structure. Lets stranded residual drift to an open edge
    //   (Pass 1 next tick drops it into any pool below) instead of being re-grown
    //   into a visible pool by RainReplenish (which gates on water > 0).
    // Integer math guarantees volume conservation throughout.
    private int SimulateStep() {
        World world = World.instance;
        int moves = 0;

        // Pass 1 — Fall
        for (int y = 1; y < world.ny; y++) {
            for (int x = 0; x < world.nx; x++) {
                Tile tile = world.GetTileAt(x, y);
                if (tile.type.solid || tile.water == 0) continue;

                Tile below = world.GetTileAt(x, y - 1);
                if (below == null || below.type.solid) continue;

                int flow = Mathf.Min(tile.water, WaterMax - below.water);
                if (flow <= 0) continue;
                tile.water  -= (ushort)flow;
                below.water += (ushort)flow;
                moves += flow;
            }
        }

        // Pass 2 — Spread (one direction, alternates each tick)
        for (int y = 0; y < world.ny; y++) {
            int xStart = flipDir ? world.nx - 1 : 0;
            int xEnd   = flipDir ? -1            : world.nx;
            int xStep  = flipDir ? -1            : 1;

            for (int x = xStart; x != xEnd; x += xStep) {
                Tile tile = world.GetTileAt(x, y);
                if (tile.type.solid || tile.water == 0) continue;

                int nx = x + xStep; // neighbour in sweep direction
                if (nx < 0 || nx >= world.nx) continue;

                Tile neighbor = world.GetTileAt(nx, y);
                if (neighbor.type.solid) continue;

                int flow = (tile.water - neighbor.water) / 2;
                if (flow <= 0) continue;
                tile.water -= (ushort)flow;
                moves += flow;

                // Diagonal fall: if the tile below the neighbor has space,
                // send as much of the flow downward as possible instead of
                // letting it sit on a tile above partially-filled water.
                if (y > 0) {
                    Tile belowNeighbor = world.GetTileAt(nx, y - 1);
                    if (belowNeighbor != null && !belowNeighbor.type.solid
                        && belowNeighbor.water < WaterMax) {
                        int diag = Mathf.Min(flow, WaterMax - belowNeighbor.water);
                        belowNeighbor.water += (ushort)diag;
                        flow -= diag;
                    }
                }
                neighbor.water += (ushort)flow;
            }
        }

        // Pass 3 — Look-ahead equalization for diff-1 slopes
        // Pass 2's diff/2 formula truncates to 0 when diff == 1, leaving water stuck in a
        // visible slope across wide bodies. For each tile that is exactly 1 unit below its
        // sweep-direction neighbor, scan further in that direction. Tiles at tile.water + 1
        // ("the plateau") are traversed; the scan stops when it finds tile.water + 2 or
        // higher (the elevated source) and pulls 1 unit from there, or when the water level
        // drops below tile.water + 1 (no source exists in this direction), or when a solid
        // tile or world edge is reached.
        for (int y = 0; y < world.ny; y++) {
            int xStart = flipDir ? world.nx - 1 : 0;
            int xEnd   = flipDir ? -1            : world.nx;
            int xStep  = flipDir ? -1            : 1;

            for (int x = xStart; x != xEnd; x += xStep) {
                Tile tile = world.GetTileAt(x, y);
                if (tile.type.solid || tile.water == 0) continue;

                int nx = x + xStep;
                if (nx < 0 || nx >= world.nx) continue;
                Tile neighbor = world.GetTileAt(nx, y);
                if (neighbor.type.solid) continue;

                if (neighbor.water != tile.water + 1) continue;

                for (int reach = 2; reach <= world.nx; reach++) {
                    int fx = x + reach * xStep;
                    if (fx < 0 || fx >= world.nx) break;
                    Tile far = world.GetTileAt(fx, y);
                    if (far.type.solid) break;
                    if (far.water >= tile.water + 2) {
                        tile.water++;
                        far.water--;
                        moves++;
                        break;
                    }
                    if (far.water < tile.water + 1) break;
                }
            }
        }

        // Pass 4 — Look-ahead drain for sub-pixel residual (dual of Pass 3)
        // Pass 3 pulls 1 unit from an elevated +2-or-higher far source down to a
        // local low tile, flattening a diff-1 staircase. Pass 4 mirrors that for
        // residual tiles (water in (0, ResidualBandMax)) stranded on a flat shelf
        // where no diff-1 staircase exists to walk down: scan along a randomly-
        // chosen direction across plateau tiles at equal water, and push 1 unit
        // to the first STRICTLY-lower tile found. Random direction instead of
        // sweep direction — alternation would just shuffle residual back and
        // forth across symmetric shelves. Tiles at equal water ("the plateau")
        // are traversed; scan stops on a strictly-lower tile (push and done),
        // a strictly-higher tile (no uphill push), or solid / world edge.
        //
        // Without this pass, abandoned sub-pixel residual sits forever and gets
        // re-grown into a visible pool by RainReplenish (which gates on water > 0,
        // not on the render threshold).
        for (int y = 0; y < world.ny; y++) {
            for (int x = 0; x < world.nx; x++) {
                Tile tile = world.GetTileAt(x, y);
                if (tile.type.solid || tile.water == 0) continue;
                if (tile.water >= ResidualBandMax) continue;

                int dir = Rng.Range(0, 2) == 0 ? -1 : 1;
                for (int reach = 1; reach <= world.nx; reach++) {
                    int fx = x + reach * dir;
                    if (fx < 0 || fx >= world.nx) break;
                    Tile far = world.GetTileAt(fx, y);
                    if (far.type.solid) break;
                    if (far.water < tile.water) {
                        tile.water--;
                        far.water++;
                        moves++;
                        break;
                    }
                    if (far.water > tile.water) break;
                }
            }
        }

        flipDir = !flipDir;
        return moves;
    }

    // Builds the R8 surface mask and uploads it to the GPU. Called every 0.2 s.
    //
    // Fast path: clears the entire byte array with a single memset, then only
    // iterates tiles that actually contain water. For a typical scene with 10% water
    // coverage this processes ~128k pixels instead of all 1.28M.
    //
    // Per water pixel, checks 8 neighbours to determine surface/interior.
    // A pixel is "surface" if any neighbour is open air (non-solid, no water there).
    // Public so worldgen paths can force a re-upload after placing water tiles
    // without waiting for the next TickUpdate. Critical when the game starts
    // paused (no tick) and when LoadDefault runs without a ReallocateGrid (same
    // world size → no OnWorldAllocated → no automatic refresh).
    public void UpdateSurfaceMask() {
        if (_surfaceTex == null) return;
        World world = World.instance;

        // Rebuild per-tile caches.
        for (int ty = 0; ty < world.ny; ty++) {
            for (int tx = 0; tx < world.nx; tx++) {
                Tile tile = world.GetTileAt(tx, ty);
                int  idx  = ty * world.nx + tx;
                _tileIsSolid[idx]       = tile.type.solid;
                _waterPixelHeights[idx] = (tile.type.solid || tile.water == 0) ? 0
                    : Mathf.RoundToInt(tile.water / (float)WaterMax * PixelsPerTile);
            }
        }

        // Wipe everything to transparent — tiles with no water stay 0 automatically.
        System.Array.Clear(_surfaceBytes, 0, _surfaceBytes.Length);
        // Decorative-zone mask is rebuilt from scratch each tick too.
        System.Array.Clear(_decorBytes, 0, _decorBytes.Length);
        // Clear per-tile tint — any tile a decorative zone doesn't stamp stays alpha=0
        // and falls through to the shader's default water color.
        System.Array.Clear(_tintBytes, 0, _tintBytes.Length);

        // Unsigned comparison folds the < 0 check into one branch.
        bool IsAir(int px, int py) {
            if ((uint)px >= (uint)_texW || (uint)py >= (uint)_texH) return false;
            int tileIdx = (py / PixelsPerTile) * world.nx + (px / PixelsPerTile);
            return !_tileIsSolid[tileIdx] && (py % PixelsPerTile) >= _waterPixelHeights[tileIdx];
        }

        // Only process tiles that have water.
        for (int ty = 0; ty < world.ny; ty++) {
            for (int tx = 0; tx < world.nx; tx++) {
                int waterHeight = _waterPixelHeights[ty * world.nx + tx];
                if (waterHeight == 0) continue;

                int pyBase = ty * PixelsPerTile;
                int pxBase = tx * PixelsPerTile;

                for (int ly = 0; ly < waterHeight; ly++) {
                    int py = pyBase + ly;
                    for (int lx = 0; lx < PixelsPerTile; lx++) {
                        int px  = pxBase + lx;
                        bool isSurface =
                            IsAir(px + 1, py) || IsAir(px - 1, py) ||
                            IsAir(px, py + 1) || IsAir(px, py - 1) ||
                            IsAir(px + 1, py + 1) || IsAir(px - 1, py + 1) ||
                            IsAir(px + 1, py - 1) || IsAir(px - 1, py - 1);
                        _surfaceBytes[py * _texW + px] = isSurface ? (byte)255 : (byte)127;
                    }
                }

                // Bleed water 2 px horizontally / 1 px downward into adjacent SOLID
                // neighbours. Solid tile sprites are baked 20×20 with 2 px bevels
                // whose corners can be transparent — leaving thin slits along
                // water/solid boundaries that read as dark background. Writing
                // interior-water (127) into the solid neighbour's edge pixels
                // covers those gaps. Empty and water neighbours are non-solid
                // (water sits in non-solid tiles) and naturally skipped, matching
                // the "only into solid tiles" requirement.
                if (tx + 1 < world.nx && _tileIsSolid[ty * world.nx + tx + 1]) {
                    for (int ly = 0; ly < waterHeight; ly++) {
                        int py = pyBase + ly;
                        _surfaceBytes[py * _texW + pxBase + PixelsPerTile    ] = 127;
                        _surfaceBytes[py * _texW + pxBase + PixelsPerTile + 1] = 127;
                    }
                }
                if (tx > 0 && _tileIsSolid[ty * world.nx + tx - 1]) {
                    for (int ly = 0; ly < waterHeight; ly++) {
                        int py = pyBase + ly;
                        _surfaceBytes[py * _texW + pxBase - 1] = 127;
                        _surfaceBytes[py * _texW + pxBase - 2] = 127;
                    }
                }
                if (ty > 0 && _tileIsSolid[(ty - 1) * world.nx + tx]) {
                    int row = (pyBase - 1) * _texW;
                    for (int lx = 0; lx < PixelsPerTile; lx++) {
                        _surfaceBytes[row + pxBase + lx] = 127;
                    }
                }
            }
        }

        // Decorative liquid zones — written into the SEPARATE _decorBytes mask (its own sprite,
        // behind mice) rather than _surfaceBytes. Each building with a {name}_w.png
        // companion answers TryGetDisplayLiquid with how full its zone draws
        // (0..1, bottom-up), its tint colour, and whether the top row
        // shimmers. Only the bottom fraction of zone pixels render; the top
        // rendered row is flagged surface (255) so it shimmers white like a
        // pond surface (unless surfaceRow is false, e.g. fountains), and
        // everything below is interior water (127). The liquid's liquidColor
        // is stamped into _tintBytes so the shader can tint the fill (rice
        // wine renders gold, water stays default blue). The fill/tint dispatch
        // (fountain / tank / processor / plain) lives in Building.
        foreach (var kvp in _decorativeZones) {
            Structure s = kvp.Key;
            DecorativeZone z = kvp.Value;

            // Non-Building zones (none today) fall back to a plain full zone.
            float   fraction   = 1f;
            Color32 tintColor  = default;
            bool    surfaceRow = true;
            if (s is Building b && !b.TryGetDisplayLiquid(out fraction, out tintColor, out surfaceRow))
                continue;

            int rows     = z.localMaxY - z.localMinY + 1;
            int fillRows = Mathf.RoundToInt(fraction * rows);
            if (fillRows <= 0) continue;
            int fillThreshold    = z.localMinY + fillRows;                       // exclusive upper bound for local Y
            int surfaceThreshold = surfaceRow ? fillThreshold - 1 : int.MinValue; // MinValue → no pixel flagged surface
            // tintColor: alpha=0 → shader's default blue; alpha=255 → liquid-specific tint.

            var worldPixels = z.worldPixels;
            var localYs     = z.localYs;
            for (int i = 0; i < worldPixels.Count; i++) {
                int ly = localYs[i];
                if (ly >= fillThreshold) continue;
                Vector2Int px = worldPixels[i];
                if ((uint)px.x >= (uint)_texW || (uint)px.y >= (uint)_texH) continue;
                _decorBytes[px.y * _texW + px.x] = (ly == surfaceThreshold) ? (byte)255 : (byte)127;
            }

            // Stamp per-tile tint once per tile covered by this zone. Tanks are 1×1 so
            // this is usually one texel, but iterating the structure's footprint keeps
            // the code robust to multi-tile liquid-holding structures.
            if (tintColor.a > 0) {
                int tx0 = s.x, tx1 = s.x + s.structType.nx - 1;
                int ty0 = s.y, ty1 = s.y + s.structType.ny - 1;
                for (int ty = ty0; ty <= ty1; ty++) {
                    for (int tx = tx0; tx <= tx1; tx++) {
                        if ((uint)tx >= (uint)world.nx || (uint)ty >= (uint)world.ny) continue;
                        int idx = (ty * world.nx + tx) * 4;
                        _tintBytes[idx + 0] = tintColor.r;
                        _tintBytes[idx + 1] = tintColor.g;
                        _tintBytes[idx + 2] = tintColor.b;
                        _tintBytes[idx + 3] = 255;
                    }
                }
            }
        }

        _surfaceTex.LoadRawTextureData(_surfaceBytes);
        _surfaceTex.Apply(false); // false = skip mipmap update
        _decorTex.LoadRawTextureData(_decorBytes);
        _decorTex.Apply(false);
        _tintTex.LoadRawTextureData(_tintBytes);
        _tintTex.Apply(false);

        // Expose to NormalsCaptureWater so the lighting pipeline can correctly
        // discard transparent water pixels when writing to the normals RT. Only the natural
        // mask is published — decorative-zone water (a thin film over an opaque building
        // backdrop) takes the building's lighting rather than the flat-water path.
        Shader.SetGlobalTexture("_WaterSurfaceTex", _surfaceTex);
    }

    // Adds 2×rainIntensity (rounded) water units to every partially-filled tile.
    // Called hourly from WeatherSystem.OnHourElapsed() with the current rainAmount.
    // Only affects tiles that already contain water (> 0) and aren't full,
    // representing rain collecting in existing puddles/pools. Scaling by
    // intensity means light drizzle tops puddles slowly, downpour quickly.
    public void RainReplenish(float rainIntensity) {
        int add = Mathf.RoundToInt(2f * rainIntensity);
        if (add <= 0) return;
        World world = World.instance;
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                if (tile.type.solid || tile.water == 0 || tile.water >= WaterMax) continue;
                tile.water = (ushort)Mathf.Min(tile.water + add, WaterMax);
            }
        }
    }

    // Rain-catching for open-air liquid-storage buildings (tanks).
    // Called hourly from WeatherSystem.OnHourElapsed() with the current rainAmount.
    // Adds rainIntensity-scaled water to any tank whose tile is sky-exposed
    // and whose storage filter allows water.
    public void RainFillTanks(float rainIntensity) {
        const int baseFillPerHourFen = 100; // 1 liang/hour at full rain; tank capacity 100 liang (~100 h full rain to fill)
        int fillFen = Mathf.RoundToInt(baseFillPerHourFen * rainIntensity);
        if (fillFen <= 0) return;

        if (!Db.itemByName.TryGetValue("water", out Item water)) {
            Debug.LogError("RainFillTanks: no 'water' item in Db");
            return;
        }

        World world = World.instance;
        foreach (Structure s in StructController.instance.GetStructures()) {
            if (s is not Building b) continue;
            if (!b.structType.isLiquidStorage) continue;
            if (b.storage == null) continue;
            if (!b.storage.allowed.TryGetValue(water.id, out bool ok) || !ok) continue;
            if (!world.IsExposedAbove(b.x, b.y)) continue;
            b.storage.Produce(water, fillFen);
        }
    }

    // Looks for a `{stem}_w.png` companion beside the main sprite (e.g. `tank_w.png`
    // next to `tank.png`) and returns the local offsets of its opaque pixels.
    // Opaque = alpha >= 128; everything else is ignored. Returns null if no companion
    // exists (this building doesn't render water). Analogous to the `_f.png` fire-mask
    // pattern used by SpriteNormalMapGenerator — the companion keeps the art workflow
    // clean by separating "where water goes" from the main sprite's visible pixels.
    //
    // The companion must match the sprite's textureRect dimensions exactly. Bottom-left
    // origin, unmirrored — the mirror flip is applied later in RegisterDecorativeWater.
    public static List<Vector2Int> ScanWaterPixels(Sprite sprite) {
        if (sprite == null || sprite.texture == null) return null;
        // sprite.name (asset name, e.g. "tank") not sprite.texture.name — after the
        // Buildings sprite-atlas binds at runtime, sprite.texture is the atlas page
        // and its name is the atlas's, not the source file's. sprite.name stays the
        // original asset stem in both atlased and loose-sprite paths.
        string stem = sprite.name;
        if (string.IsNullOrEmpty(stem)) return null;
        Texture2D mask = Resources.Load<Texture2D>($"Sprites/Buildings/{stem}_w");
        if (mask == null) return null; // no companion — this building doesn't render water
        if (!mask.isReadable) {
            Debug.LogError($"ScanWaterPixels: {stem}_w.png is not Read/Write enabled — BuildingSpritePostprocessor should handle this automatically");
            return null;
        }
        // Round, don't truncate: an atlased sprite's textureRect can read a hair under the integer
        // size (e.g. foundry 31.92 vs 32) from atlas-UV float error — truncating would spuriously
        // reject a correctly-sized mask. Every loose sprite's rect is already integer.
        int sprW = Mathf.RoundToInt(sprite.textureRect.width);
        int sprH = Mathf.RoundToInt(sprite.textureRect.height);
        if (mask.width != sprW || mask.height != sprH) {
            Debug.LogError($"ScanWaterPixels: {stem}_w.png size {mask.width}×{mask.height} does not match sprite {sprW}×{sprH}");
            return null;
        }
        Color32[] pixels = mask.GetPixels32();
        List<Vector2Int> offsets = null;
        for (int ly = 0; ly < sprH; ly++) {
            for (int lx = 0; lx < sprW; lx++) {
                if (pixels[ly * mask.width + lx].a >= 128) {
                    if (offsets == null) offsets = new List<Vector2Int>();
                    offsets.Add(new Vector2Int(lx, ly));
                }
            }
        }
        return offsets;
    }

    // Registers a structure's water-marker pixels for overlay each tick.
    // Converts local sprite offsets to world-pixel coordinates, accounting for mirroring.
    // Also caches per-pixel local Y and the local Y range, so UpdateSurfaceMask can
    // do fill-level rendering for liquid-storage buildings without re-scanning the sprite.
    // Called by StructController.Place() for any structure with waterPixelOffsets.
    public void RegisterDecorativeWater(Structure s) {
        if (s.waterPixelOffsets == null || s.waterPixelOffsets.Count == 0) return;
        int sprW = (int)s.sprite.textureRect.width;
        int count = s.waterPixelOffsets.Count;
        var worldPixels = new List<Vector2Int>(count);
        var localYs     = new List<int>(count);
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (Vector2Int local in s.waterPixelOffsets) {
            int lx = s.mirrored ? (sprW - 1 - local.x) : local.x;
            worldPixels.Add(new Vector2Int(s.x * PixelsPerTile + lx, s.y * PixelsPerTile + local.y));
            localYs.Add(local.y);
            if (local.y < minY) minY = local.y;
            if (local.y > maxY) maxY = local.y;
        }
        _decorativeZones[s] = new DecorativeZone {
            worldPixels = worldPixels,
            localYs     = localYs,
            localMinY   = minY,
            localMaxY   = maxY,
        };
    }

    // Removes a structure's water-marker pixels. Called from Structure.Destroy().
    public void UnregisterDecorativeWater(Structure s) {
        _decorativeZones.Remove(s);
    }

    // Zeros all tile water and clears the surface mask texture.
    // Called from WorldController.ClearWorld() before regenerating the world.
    public void ClearWater() {
        World world = World.instance;
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                world.GetTileAt(x, y).water = 0;
            }
        }
        if (_surfaceTex == null) return;
        System.Array.Clear(_surfaceBytes, 0, _surfaceBytes.Length);
        _surfaceTex.LoadRawTextureData(_surfaceBytes);
        _surfaceTex.Apply(false);
        if (_tintTex != null) {
            System.Array.Clear(_tintBytes, 0, _tintBytes.Length);
            _tintTex.LoadRawTextureData(_tintBytes);
            _tintTex.Apply(false);
        }
    }
}
