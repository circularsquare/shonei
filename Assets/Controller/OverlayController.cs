using UnityEngine;

// World-space data-overlay system (Step 4). Paints a per-tile colour layer over the world on the
// Unlit layer (so it dims toward night like the other overlays) using ONE SpriteRenderer backed by
// an nx×ny RGBA32 texture — 1 texel per tile, the WaterController tint pattern rendered as a sprite.
// Far cheaper than a pool of per-tile highlight objects, and reusable: each overlay is just a Mode
// + a fill method; the texture/quad plumbing is shared.
//
// Overlays:
//   • SearchRange — a selected mouse's work-search range: the anchor TERRITORY box plus the smaller
//     CONVENIENCE box around the mouse (see Nav.WithinWorkRange), over a darkened wash, faded by
//     distance so lower-priority edges read fainter. Only tiles in the same nav component as the
//     mouse are highlighted (walled-off / cross-water / solid tiles stay dark). It does NOT subtract
//     A* path cost, so a same-component-but-path-far tile can still show — a close approximation of
//     WithinWorkRange's gate, not the exact reachable-within-cost set. Entered from the mouse
//     InfoPanel's "range" button; follows the mouse, regenerating only when it changes tile.
//   • FootTraffic — a heat map of how often each tile carries a moving mouse (FootTrafficSystem),
//     normalised against the live max, over the same dark wash. Hovering a trafficked tile reads its
//     rate in mice/hour. Entered from the data-views menu (DataViewMenu).
//   • SoilMoisture — per-tile soil moisture (Tile.moisture, 0–100 on solid tiles) as a blue wash;
//     hovering a soil tile reads its moisture %. Also from the data-views menu.
//
// The two data-views modes have no subject and are refreshed on a timer because their underlying
// fields drift over time. A left-click anywhere in the world exits whichever overlay is up.
public class OverlayController : MonoBehaviour {
    public static OverlayController instance { get; private set; }

    private enum Mode { None, SearchRange, FootTraffic, SoilMoisture }
    private Mode mode = Mode.None;
    private Animal subject;                                   // mouse whose range is shown (SearchRange only)
    private int lastX = int.MinValue, lastY = int.MinValue;   // regen only when the subject changes tile

    private const float DataRefreshInterval = 0.5f;          // seconds between rebuilds for the timer-driven (non-subject) modes
    private float refreshTimer;

    private GameObject go;
    private SpriteRenderer sr;
    private Texture2D tex;
    private Color32[] pixels;
    private int nx, ny;

    // Last tile we showed a hover tooltip for, so we only re-Show on change (TooltipSystem follows
    // the cursor on its own). long.MinValue = no tooltip currently shown by us.
    private long tooltipTileKey = long.MinValue;

    // Territory = faint cool wash (the home/flag work zone); convenience = brighter warm (grab
    // underfoot). Both fade from centre to edge. Tune freely — purely cosmetic.
    private static readonly Color TerritoryColor   = new Color(0.30f, 0.55f, 1.00f);
    private static readonly Color ConvenienceColor = new Color(1.00f, 0.80f, 0.30f);

    // Foot-traffic heat: warm amber scaled by (perceptual) normalised intensity against the dark wash.
    private static readonly Color TrafficColor     = new Color(1.00f, 0.70f, 0.20f);
    // Soil moisture: cool blue scaled by moisture fraction (0–1) against the dark wash.
    private static readonly Color MoistureColor    = new Color(0.30f, 0.60f, 1.00f);

    void Awake() {
        if (instance != null && instance != this) { Destroy(this); return; }
        instance = this;
    }

    public bool IsActive => mode != Mode.None;

    // Show the work-search range of `a`. Re-clicking with a different mouse retargets.
    public void ShowSearchRange(Animal a) {
        if (a == null) return;
        mode = Mode.SearchRange;
        subject = a;
        lastX = int.MinValue; // force a rebuild
        EnsureLayer();
        if (go != null) go.SetActive(true);
        Rebuild();
    }

    // Show the foot-traffic heat map. No subject; refreshed on a timer while up.
    public void ShowFootTraffic() {
        mode = Mode.FootTraffic;
        subject = null;
        refreshTimer = 0f;
        EnsureLayer();
        if (go != null) go.SetActive(true);
        Rebuild();
    }

    // Show the soil-moisture overlay. No subject; refreshed on a timer while up.
    public void ShowSoilMoisture() {
        mode = Mode.SoilMoisture;
        subject = null;
        refreshTimer = 0f;
        EnsureLayer();
        if (go != null) go.SetActive(true);
        Rebuild();
    }

    public void Hide() {
        mode = Mode.None;
        subject = null;
        if (go != null) go.SetActive(false);
        ClearTooltip();
    }

    void Update() {
        if (mode == Mode.None) return;
        var es = UnityEngine.EventSystems.EventSystem.current;
        bool overUI = es != null && es.IsPointerOverGameObject();
        // A world left-click (not on UI) dismisses the overlay. The click still falls through to
        // MouseController for its normal selection — dismiss is just a side effect.
        if (Input.GetMouseButtonDown(0) && !overUI) { Hide(); return; }

        if (mode == Mode.SearchRange) {
            if (subject == null || subject.go == null) { Hide(); return; }
            int sx = (int)subject.x, sy = (int)subject.y;
            if (sx != lastX || sy != lastY) Rebuild();   // follow the mouse, throttled to tile changes
        } else {
            // Timer-driven modes (FootTraffic, SoilMoisture) — their fields drift, so rebuild periodically.
            refreshTimer += Time.deltaTime;
            if (refreshTimer >= DataRefreshInterval) { refreshTimer = 0f; Rebuild(); }
        }
        UpdateTileTooltip(overUI);
    }

    // ── Hover readout ────────────────────────────────────────────────────────
    // While the overlay is up, hovering a tile shows what region it's in and how far it is from the
    // region's centre. Only re-Shows when the hovered tile changes (TooltipSystem follows the cursor
    // itself). Hidden over UI / off-world / outside any region.
    void UpdateTileTooltip(bool overUI) {
        if (overUI || Camera.main == null || World.instance == null) { ClearTooltip(); return; }
        Vector3 wp = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Tile t = World.instance.GetTileAt(wp.x, wp.y);
        if (t == null) { ClearTooltip(); return; }
        string title = null, body = null;
        bool got = false;
        if (mode == Mode.SearchRange)       got = DescribeSearchTile(t.x, t.y, out title, out body);
        else if (mode == Mode.FootTraffic)  got = DescribeFootTrafficTile(t.x, t.y, out title, out body);
        else if (mode == Mode.SoilMoisture) got = DescribeMoistureTile(t.x, t.y, out title, out body);
        if (got) {
            long key = ((long)(t.x + 100000) << 20) | (uint)(t.y + 100000);
            if (key != tooltipTileKey) { tooltipTileKey = key; TooltipSystem.Show(title, body); }
        } else {
            ClearTooltip();
        }
    }

    // Foot-traffic readout — only trafficked tiles get a tooltip (skip the dark, idle field).
    bool DescribeFootTrafficTile(int tx, int ty, out string title, out string body) {
        title = null; body = null;
        var ft = FootTrafficSystem.instance;
        if (ft == null) return false;
        float mph = ft.MicePerHourAt(tx, ty);
        if (mph < 0.1f) return false;
        title = "foot traffic";
        body  = mph.ToString("0.0") + " mice/hr";
        return true;
    }

    // Soil-moisture readout — only solid (soil) tiles carry moisture, so only they get a tooltip.
    // Shows saturation: moisture as a % of the material's capacity (so 20/20 on slate reads 100%).
    bool DescribeMoistureTile(int tx, int ty, out string title, out string body) {
        title = null; body = null;
        World w = World.instance;
        Tile t = w != null ? w.GetTileAt(tx, ty) : null;
        if (t == null || !t.type.solid) return false;
        int cap = t.type.moistureCapacity;
        if (cap <= 0) return false; // material holds no moisture
        title = "soil moisture";
        body  = Mathf.RoundToInt(t.moisture * 100f / cap) + "%";
        return true;
    }

    void ClearTooltip() {
        if (tooltipTileKey == long.MinValue) return;   // we have none up — don't fight UI tooltips
        tooltipTileKey = long.MinValue;
        TooltipSystem.Hide();
    }

    // Classifies a tile for the search-range overlay. Convenience wins where both apply, matching
    // the drawn-on-top amber. Returns false (no tooltip) outside both regions.
    bool DescribeSearchTile(int tx, int ty, out string title, out string body) {
        title = null; body = null;
        if (subject == null) return false;
        // Only reachable tiles are highlighted, so only they get a readout.
        World w = World.instance;
        Tile here = w != null ? w.GetTileAt(tx, ty) : null;
        if (here == null || !w.graph.SameComponent(subject.PathStartNode(), here.node)) return false;
        int mx = (int)subject.x, my = (int)subject.y;
        Tile anchor = subject.WorkAnchorTile;
        int ax = anchor != null ? anchor.x : mx, ay = anchor != null ? anchor.y : my;
        int dMouse  = Mathf.Abs(tx - mx) + Mathf.Abs(ty - my);
        int dAnchor = Mathf.Abs(tx - ax) + Mathf.Abs(ty - ay);
        if (dMouse <= Task.WorkConvenienceRadius) {
            title = "convenience";
            body  = dMouse + " / " + Task.WorkConvenienceRadius + " from mouse";
            return true;
        }
        if (dAnchor <= Task.MediumFindRadius) {
            title = anchor != null ? "home range" : "work range";
            body  = dAnchor + " / " + Task.MediumFindRadius + (anchor != null ? " from home" : " from mouse");
            return true;
        }
        return false;
    }

    // Lazily build (and resize on world change) the overlay GameObject, texture, and sprite. The
    // sprite is PPU=1 with a bottom-left pivot, placed at (-0.5,-0.5) so texel (i,j) covers tile
    // (i,j)'s cell [i-0.5,i+0.5]×[j-0.5,j+0.5] exactly — same tile↔texel mapping as WaterController.
    void EnsureLayer() {
        World w = World.instance;
        if (w == null) return;
        if (go == null) {
            go = new GameObject("DataOverlay");
            go.transform.SetParent(transform, false);
            int unlit = LayerMask.NameToLayer("Unlit");
            if (unlit >= 0) go.layer = unlit;
            sr = go.AddComponent<SpriteRenderer>();
            Material m = SpriteMaterialUtil.OverlayAmbientMaterial;
            if (m != null) sr.sharedMaterial = m;
            sr.sortingOrder = 1000; // above world content; Unlit camera sorts by order
            LightReceiverUtil.SetSortBucket(sr);
        }
        if (tex == null || nx != w.nx || ny != w.ny) {
            nx = w.nx; ny = w.ny;
            tex = new Texture2D(nx, ny, TextureFormat.RGBA32, false) {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp
            };
            pixels = new Color32[nx * ny];
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, nx, ny), new Vector2(0f, 0f), 1f);
            go.transform.position = new Vector3(-0.5f, -0.5f, 0f);
        }
    }

    void Rebuild() {
        if (mode == Mode.None) return;
        EnsureLayer();
        if (tex == null) return;
        if (mode == Mode.SearchRange) {
            if (subject == null) return;
            lastX = (int)subject.x; lastY = (int)subject.y;
            FillSearchRange(subject);
        } else if (mode == Mode.FootTraffic) {
            FillFootTraffic();
        } else if (mode == Mode.SoilMoisture) {
            FillSoilMoisture();
        }
        tex.SetPixels32(pixels);
        tex.Apply();
    }

    // Darken wash applied to the whole world so the coloured regions pop; the value falloff is then
    // carried by the region colour fading toward this same black at its edges.
    private const float BackgroundDarken = 0.70f; // black alpha over everything
    private const float RegionAlpha      = 0.82f; // alpha of region tiles (sits just above the wash)
    private const float EdgeBright       = 0.25f; // region colour fraction at the box edge (1.0 at centre)

    void FillSearchRange(Animal a) {
        // 1) Darken everything — a ~70% black wash.
        var dark = new Color32(0, 0, 0, (byte)(BackgroundDarken * 255f));
        for (int i = 0; i < pixels.Length; i++) pixels[i] = dark;

        // 2) Composite the region boxes on top, but only on tiles the mouse can actually reach
        //    (same nav component) — so tiles walled off / across water stay dark. Non-standable /
        //    solid tiles are component -1, so they drop out too.
        Node reachFrom = a.PathStartNode();
        int mx = (int)a.x, my = (int)a.y;
        Tile anchor = a.WorkAnchorTile;
        int ax = anchor != null ? anchor.x : mx;  // homeless: territory centres on the mouse
        int ay = anchor != null ? anchor.y : my;
        StampBox(ax, ay, Task.MediumFindRadius, TerritoryColor, reachFrom);       // territory
        StampBox(mx, my, Task.WorkConvenienceRadius, ConvenienceColor, reachFrom); // convenience, on top
    }

    // Heat map of how often each tile carries a moving mouse. Darkens the world, then lights tiles
    // in warm amber scaled by intensity normalised against the live max — so the busiest tile is
    // always full-bright and the scale auto-adjusts. A sqrt makes light traffic visible (perceptual
    // spread). Untrafficked tiles (including all solid ones) keep the dark wash.
    void FillFootTraffic() {
        var dark = new Color32(0, 0, 0, (byte)(BackgroundDarken * 255f));
        for (int i = 0; i < pixels.Length; i++) pixels[i] = dark;

        var ft = FootTrafficSystem.instance;
        if (ft == null) return;
        float max = ft.MaxIntensity();
        if (max <= 0.0001f) return; // no traffic recorded yet — leave the wash
        float inv = 1f / max;

        byte a = (byte)(RegionAlpha * 255f);
        for (int y = 0; y < ny; y++) {
            for (int x = 0; x < nx; x++) {
                float v = ft.IntensityAt(x, y) * inv;
                if (v <= 0f) continue;
                float b = Mathf.Sqrt(Mathf.Clamp01(v));
                Color c = TrafficColor * b;
                pixels[y * nx + x] = new Color32(
                    (byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), a);
            }
        }
    }

    // Soil-moisture wash. Darkens the world, then tints each solid tile blue by its SATURATION —
    // moisture relative to that material's capacity (tile.type.moistureCapacity), which varies by
    // material (e.g. ~20 on slate). So a fully-saturated low-cap tile reads as bright as a saturated
    // earth tile. Dry soil reads near-dark; wet soil glows, so damp pockets are easy to spot.
    void FillSoilMoisture() {
        var dark = new Color32(0, 0, 0, (byte)(BackgroundDarken * 255f));
        for (int i = 0; i < pixels.Length; i++) pixels[i] = dark;

        World w = World.instance;
        if (w == null) return;
        byte a = (byte)(RegionAlpha * 255f);
        for (int y = 0; y < ny; y++) {
            for (int x = 0; x < nx; x++) {
                Tile t = w.GetTileAt(x, y);
                if (t == null || !t.type.solid) continue;
                int cap = t.type.moistureCapacity;
                if (cap <= 0) continue; // material holds no moisture
                float m = t.moisture / (float)cap;
                if (m <= 0f) continue;
                Color c = MoistureColor * Mathf.Clamp01(m);
                pixels[y * nx + x] = new Color32(
                    (byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), a);
            }
        }
    }

    // Stamps a Manhattan diamond of radius r centred at (cx,cy): the region colour at full strength
    // in the centre, fading toward EdgeBright at the edge so the value falloff reads against the dark
    // wash. Only reachable tiles (same nav component as reachFrom) are coloured; others keep the
    // dark wash. Always overwrites, so the later box (convenience) wins where the two overlap.
    void StampBox(int cx, int cy, int r, Color col, Node reachFrom) {
        World w = World.instance;
        for (int dy = -r; dy <= r; dy++) {
            int ty = cy + dy;
            if (ty < 0 || ty >= ny) continue;
            for (int dx = -r; dx <= r; dx++) {
                int tx = cx + dx;
                if (tx < 0 || tx >= nx) continue;
                int dist = Mathf.Abs(dx) + Mathf.Abs(dy);
                if (dist > r) continue; // Manhattan diamond, not a square
                Tile tile = w.GetTileAt(tx, ty);
                if (tile == null || !w.graph.SameComponent(reachFrom, tile.node)) continue; // unreachable → stay dark
                float t = r > 0 ? (float)dist / r : 0f;
                float bright = Mathf.Lerp(1f, EdgeBright, t);
                Color c = col * bright; // fade toward black; alpha stays constant
                pixels[ty * nx + tx] = new Color32(
                    (byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), (byte)(RegionAlpha * 255f));
            }
        }
    }
}
