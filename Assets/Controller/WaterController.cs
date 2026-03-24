using System.Collections;
using UnityEngine;

/// <summary>
/// Simulates and renders a cellular-automaton water fluid system.
/// Water is stored as a ushort (0–160, fixed-point) on each Tile. Each TickUpdate:
///   1. SimulateStep — water falls downward then spreads laterally (volume-preserving).
///   2. UpdateSurfaceMask — builds a 1-byte-per-pixel surface mask and uploads it to the GPU.
///
/// GPU rendering (Assets/Lighting/Water.shader) is deliberately simple: one texture sample
/// per pixel, then either discard / surface color / shimmer lerp. All neighbour-air detection
/// happens on the CPU every 0.2 s instead of in the shader every frame, which is much cheaper
/// on slow/integrated GPUs due to lower texture bandwidth and no branch divergence.
///
/// Surface mask encoding (TextureFormat.R8, one byte per game pixel):
///   0   = transparent  (no water)
///   127 = interior water
///   255 = surface water (at least one of 8 neighbours is open air)
///
/// Called every 0.2 seconds from World.Update (same cadence as inventory tick).
/// </summary>
public class WaterController : MonoBehaviour {
    public static WaterController instance { get; private set; }

    [Header("Water Colors")]
    [SerializeField] Color waterColorDark  = new Color(0.08f, 0.35f, 0.85f, 0.55f);
    [SerializeField] Color waterColorLight = new Color(0.18f, 0.50f, 0.95f, 0.50f);
    [SerializeField] Color surfaceColor    = new Color(1.00f, 1.00f, 1.00f, 0.75f);

    // Asset pixels per tile edge. 16 PPU assets → 16 game pixels per tile edge.
    private const int PixelsPerTile = 16;

    // Surface mask: R8 texture, one byte per game pixel.
    private Texture2D _surfaceTex;
    private byte[]    _surfaceBytes;  // reused buffer — no per-tick allocation
    private int       _texW, _texH;

    // Per-tile caches rebuilt at the start of each UpdateSurfaceMask.
    private int[]  _waterPixelHeights; // game pixel rows filled per tile (0–PixelsPerTile)
    private bool[] _tileIsSolid;       // true if tile.type.solid

    private Material _waterMat;

    /// <summary>
    /// Internal fixed-point scale: 10 internal units = 1 display unit (tile fully filled at 160).
    /// Scaling up from 16 eliminates the integer-truncation dead zone in the spread formula
    /// (diff/2 == 0 when diff == 1), which would otherwise leave water in a staircase pattern.
    /// The dead zone shrinks to 1/10 of a visual unit — sub-pixel, undetectable.
    /// </summary>
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

        // Create material and push colours.
        _waterMat = new Material(Shader.Find("Water/WaterSurface"));
        if (_waterMat == null) {
            Debug.LogError("WaterController: could not find shader 'Water/WaterSurface'");
            yield break;
        }
        _waterMat.SetColor("_WaterColorDark",  waterColorDark);
        _waterMat.SetColor("_WaterColorLight", waterColorLight);
        _waterMat.SetColor("_SurfaceColor",    surfaceColor);
        _waterMat.SetVector("_WorldPixelSize", new Vector4(_texW, _texH, 0, 0));
        _waterMat.SetTexture("_SurfaceTex", _surfaceTex);

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
        sr.sortingOrder = 2;

        // Sync with any water already present (e.g. from world gen or save load).
        UpdateSurfaceMask();
    }

    /// <summary>Called by World.Update every 0.2 seconds.</summary>
    public void TickUpdate() {
        SimulateStep();
        UpdateSurfaceMask();
    }

    /// <summary>
    /// One cellular-automaton step.
    /// Pass 1 (bottom-to-top): pour water straight down into the tile below.
    /// Pass 2 (bottom-to-top, alternating L/R): equalize with one horizontal neighbor (diff/2).
    /// Pass 3 (same direction as Pass 2): look-ahead equalization for diff-1 slopes.
    ///   When a tile is exactly 1 unit below its neighbor, scan further in that direction
    ///   for a tile at +2 or higher (ignoring plateau tiles at +1). If found, pull 1 unit
    ///   from that elevated source to the low tile, flattening the slope step by step.
    ///   This prevents CA water from getting permanently stuck in a visible staircase.
    /// Integer math guarantees volume conservation throughout.
    /// </summary>
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
                tile.water     -= (ushort)flow;
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

    /// <summary>
    /// Builds the R8 surface mask and uploads it to the GPU. Called every 0.2 s.
    ///
    /// Fast path: clears the entire byte array with a single memset, then only
    /// iterates tiles that actually contain water. For a typical scene with 10% water
    /// coverage this processes ~128k pixels instead of all 1.28M.
    ///
    /// Per water pixel, checks 8 neighbours to determine surface/interior.
    /// A pixel is "surface" if any neighbour is open air (non-solid, no water there).
    /// </summary>
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
            }
        }

        _surfaceTex.LoadRawTextureData(_surfaceBytes);
        _surfaceTex.Apply(false); // false = skip mipmap update

        // Expose to NormalsCaptureWater so the lighting pipeline can correctly
        // discard transparent water pixels when writing to the normals RT.
        Shader.SetGlobalTexture("_WaterSurfaceTex", _surfaceTex);
    }

    /// <summary>
    /// Adds 2 water units to every partially-filled tile.
    /// Called by WeatherSystem.OnHourElapsed() when it is raining.
    /// Only affects tiles that already contain water (> 0) and aren't full,
    /// representing rain collecting in existing puddles/pools.
    /// </summary>
    public void RainReplenish() {
        World world = World.instance;
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Tile tile = world.GetTileAt(x, y);
                if (tile.type.solid || tile.water == 0 || tile.water >= WaterMax) continue;
                tile.water = (ushort)Mathf.Min(tile.water + 2, WaterMax);
            }
        }
    }

    /// <summary>
    /// Zeros all tile water and clears the surface mask texture.
    /// Called from WorldController.ClearWorld() before regenerating the world.
    /// </summary>
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
    }
}
