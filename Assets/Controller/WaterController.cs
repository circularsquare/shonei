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
    // Solid 0.9-alpha base layer that sits behind BackgroundTile. Occludes the
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
    // The dead zone shrinks to 1/10 of a visual unit — sub-pixel, undetectable.
    public const ushort WaterMax = 160;

    // Alternates each SimulateStep to prevent directional spread bias.
    private bool flipDir = false;

    void Awake() {
        if (instance != null) {
            Debug.LogError("WaterController: duplicate instance detected");
            return;
        }
        instance = this;
    }

    IEnumerator Start() {
        // Wait one frame so WorldController.Start() has created all tile references.
        yield return null;

        World world = World.instance;

        _texW = world.nx * PixelsPerTile;
        _texH = world.ny * PixelsPerTile;
        _waterPixelHeights = new int[world.nx * world.ny];
        _tileIsSolid       = new bool[world.nx * world.ny];

        // R8 surface mask: 1 byte per game pixel — 1.25 MB for a 100×50 tile world.
        _surfaceTex   = new Texture2D(_texW, _texH, TextureFormat.R8, false);
        _surfaceTex.filterMode = FilterMode.Point;
        _surfaceBytes = new byte[_texW * _texH];

        // RGBA32 per-tile tint: 20 KB for a 100×50 world. Point filter is critical —
        // bilinear would bleed colors across tile borders.
        _tintTex = new Texture2D(world.nx, world.ny, TextureFormat.RGBA32, false);
        _tintTex.filterMode = FilterMode.Point;
        _tintBytes = new byte[world.nx * world.ny * 4];

        // Create material and push colours.
        if (waterShader == null) {
            Debug.LogError("WaterController: waterShader unassigned in Inspector — assign Water/WaterSurface (Assets/Lighting/Water.shader)");
            yield break;
        }
        _waterMat = new Material(waterShader);
        _waterMat.SetColor("_WaterColorDark",  waterColorDark);
        _waterMat.SetColor("_WaterColorLight", waterColorLight);
        _waterMat.SetColor("_SurfaceColor",    surfaceColor);
        _waterMat.SetVector("_WorldPixelSize", new Vector4(_texW, _texH, 0, 0));
        _waterMat.SetTexture("_SurfaceTex", _surfaceTex);
        _waterMat.SetTexture("_TintTex",    _tintTex);

        if (waterUnderlayShader == null) {
            Debug.LogError("WaterController: waterUnderlayShader unassigned in Inspector — assign Water/WaterUnderlay (Assets/Lighting/WaterUnderlay.shader)");
            yield break;
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

        GameObject go = new GameObject("WaterSprite");
        go.transform.position   = new Vector3(-0.5f, -0.5f, 0f);
        go.transform.localScale = new Vector3(world.nx, world.ny, 1f);

        int waterLayer = LayerMask.NameToLayer("Water");
        if (waterLayer < 0)
            Debug.LogError("WaterController: 'Water' layer not found — create it in Edit → Project Settings → Tags and Layers");
        else
            go.layer = waterLayer;

        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite      = waterSprite;
        sr.material    = _waterMat;
        // Render behind tiles (0) but in front of the background wall (-10).
        // Putting water at the back lets the solid tile body hide the bleed
        // pixels we write into solid neighbours (see UpdateSurfaceMask) — the
        // bleed only shows through the tile sprite's transparent bevel gaps,
        // which is exactly the gap-fill effect we want. Decorative water
        // zones (fountains, tanks) rely on their main building sprite having
        // the water region authored as transparent so the shimmer reads
        // through.
        sr.sortingOrder = -5;
        LightReceiverUtil.SetSortBucket(sr);

        // Underlay sprite — same geometry, same Water Unity layer, same shared
        // surface mask. Sorts at -15 (behind BackgroundTile at -10) so the cave
        // wall draws on top, preserving the underground see-through. Above ground
        // (no BackgroundTile pixels) the 0.9-alpha base occludes the parallax
        // sky painting that the front 0.5-alpha layer would otherwise let through.
        // No shimmer/sparkles — those come from the front Water.shader only,
        // avoiding the doubled-sparkle blowout that two stacked shimmer layers
        // would produce.
        GameObject underGo = new GameObject("WaterSpriteUnderlay");
        underGo.transform.position   = go.transform.position;
        underGo.transform.localScale = go.transform.localScale;
        if (waterLayer >= 0) underGo.layer = waterLayer;
        SpriteRenderer underSr = underGo.AddComponent<SpriteRenderer>();
        underSr.sprite       = waterSprite; // share the 1×1 white sprite
        underSr.material     = _underlayMat;
        underSr.sortingOrder = -15;
        LightReceiverUtil.SetSortBucket(underSr);

        // Sync with any water already present (e.g. from world gen or save load).
        UpdateSurfaceMask();
    }

    // Called by World.Update every 0.2 seconds.
    public void TickUpdate() {
        SimulateStep();
        UpdateSurfaceMask();
    }

    // One cellular-automaton step.
    // Pass 1 (bottom-to-top): pour water straight down into the tile below.
    // Pass 2 (bottom-to-top, alternating L/R): equalize with one horizontal neighbor (diff/2).
    // Pass 3 (same direction as Pass 2): look-ahead equalization for diff-1 slopes.
    //   When a tile is exactly 1 unit below its neighbor, scan further in that direction
    //   for a tile at +2 or higher (ignoring plateau tiles at +1). If found, pull 1 unit
    //   from that elevated source to the low tile, flattening the slope step by step.
    //   This prevents CA water from getting permanently stuck in a visible staircase.
    // Integer math guarantees volume conservation throughout.
    private void SimulateStep() {
        World world = World.instance;

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
                        break;
                    }
                    if (far.water < tile.water + 1) break;
                }
            }
        }

        flipDir = !flipDir;
    }

    // Builds the R8 surface mask and uploads it to the GPU. Called every 0.2 s.
    //
    // Fast path: clears the entire byte array with a single memset, then only
    // iterates tiles that actually contain water. For a typical scene with 10% water
    // coverage this processes ~128k pixels instead of all 1.28M.
    //
    // Per water pixel, checks 8 neighbours to determine surface/interior.
    // A pixel is "surface" if any neighbour is open air (non-solid, no water there).
    private void UpdateSurfaceMask() {
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

        // Overlay decorative water zones. Two gating modes:
        //   - Fountains (Building with a reservoir): binary — all pixels render if HasFuel().
        //   - Tanks (structType.isLiquidStorage): fill-level — only the bottom fraction of
        //     pixels render, scaled to the stored liquid's quantity vs total capacity.
        //     The top row of rendered pixels is flagged surface (255) so it shimmers white
        //     like a pond surface; everything below is interior water (127).
        // Tanks also stamp their liquid's liquidColor into _tintBytes so the shader
        // can tint the fill (e.g. soymilk renders beige, water stays default blue).
        foreach (var kvp in _decorativeZones) {
            Structure s = kvp.Key;
            DecorativeZone z = kvp.Value;

            int fillThreshold    = int.MaxValue; // exclusive upper bound for local Y
            int surfaceThreshold = int.MinValue; // local Y that becomes surface pixels
            Color32 tintColor    = default;      // alpha=0 → don't stamp (fallback path)
            bool skip = false;

            if (s is Building b) {
                if (b.reservoir != null) {
                    if (!b.reservoir.HasFuel() || b.IsBroken) skip = true;
                } else if (b.structType.isLiquidStorage && b.storage != null) {
                    // Tanks hold one liquid at a time — find the first non-empty liquid stack
                    // so we can drive both the fill level and the tint from the same source.
                    Item liquid = null;
                    int  liquidFen = 0;
                    foreach (ItemStack st in b.storage.itemStacks) {
                        if (st?.item != null && st.item.isLiquid && st.quantity > 0) {
                            liquid = st.item;
                            liquidFen = st.quantity;
                            break;
                        }
                    }
                    if (liquid == null) { skip = true; }
                    else {
                        int capacityFen = b.storage.stackSize * b.storage.nStacks;
                        int rows        = z.localMaxY - z.localMinY + 1;
                        int fillRows    = capacityFen > 0
                            ? Mathf.RoundToInt(liquidFen / (float)capacityFen * rows)
                            : 0;
                        if (fillRows <= 0) skip = true;
                        else {
                            fillThreshold    = z.localMinY + fillRows; // exclusive
                            surfaceThreshold = fillThreshold - 1;
                            tintColor        = liquid.liquidColor; // alpha=0 when liquid has no hex
                        }
                    }
                }
            }
            if (skip) continue;

            var worldPixels = z.worldPixels;
            var localYs     = z.localYs;
            for (int i = 0; i < worldPixels.Count; i++) {
                int ly = localYs[i];
                if (ly >= fillThreshold) continue;
                Vector2Int px = worldPixels[i];
                if ((uint)px.x >= (uint)_texW || (uint)px.y >= (uint)_texH) continue;
                _surfaceBytes[px.y * _texW + px.x] = (ly == surfaceThreshold) ? (byte)255 : (byte)127;
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
        _tintTex.LoadRawTextureData(_tintBytes);
        _tintTex.Apply(false);

        // Expose to NormalsCaptureWater so the lighting pipeline can correctly
        // discard transparent water pixels when writing to the normals RT.
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
        string stem = sprite.texture.name;
        if (string.IsNullOrEmpty(stem)) return null;
        Texture2D mask = Resources.Load<Texture2D>($"Sprites/Buildings/{stem}_w");
        if (mask == null) return null; // no companion — this building doesn't render water
        if (!mask.isReadable) {
            Debug.LogError($"ScanWaterPixels: {stem}_w.png is not Read/Write enabled — BuildingSpritePostprocessor should handle this automatically");
            return null;
        }
        int sprW = (int)sprite.textureRect.width;
        int sprH = (int)sprite.textureRect.height;
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
