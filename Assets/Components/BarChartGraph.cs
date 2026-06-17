using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// BarChartGraph — a generic historic bar chart rendered into a Texture2D and shown
// through a RawImage. Reusable for any per-period series (food per day, etc.); feed
// it value arrays via SetData.
//
// Single- or double-sided:
//   • SetData(up, null, …)  → bars rise from a baseline at the bottom.
//   • SetData(up, down, …)  → diverging chart: a baseline runs across the middle,
//                             `up` bars rise above it and `down` bars drop below it,
//                             sharing one scale so equal values read as equal heights.
//
// Fixed-width slots: the plot is divided into `slotCount` equal columns and bars fill
// in FROM THE RIGHT (newest on the right), so every bar is the same width regardless
// of how many days of data exist yet. A no-data day is an empty slot; a zero-value
// day is a real but zero-height bar.
//
// Hovering a day-column shows a tooltip with that day's value(s) via the shared
// TooltipSystem. The optional `lastIsLive` flag tints the newest bars lighter to mark
// an in-progress period and labels its tooltip "today".
//
// A texture (not a custom Graphic mesh) is used for the same reasons as PriceGraph:
// it renders reliably as a runtime-added component and suits the pixel-art look —
// point filtering keeps the bars crisp.
[RequireComponent(typeof(RawImage))]
public class BarChartGraph : MonoBehaviour, IPointerMoveHandler, IPointerExitHandler {
    // ── Tuning ──────────────────────────────────────────────────────────────
    const int   AxisThickness = 1;     // px — axis / baseline line width
    const int   BarGap        = 1;     // px — gap between adjacent bar columns
    const float RangePadFrac  = 0.1f;  // headroom beyond the largest bar

    static readonly Color32 BgColor   = new Color32(0, 0, 0, 0);        // transparent — wood frame shows through
    static readonly Color32 AxisColor = new Color32(74, 58, 40, 255);   // dark brown — axes / baseline

    static readonly Color32 UpColor       = new Color32(125, 90, 0, 255);   // dark amber — produced (up)
    static readonly Color32 UpLiveColor   = new Color32(181, 138, 54, 255); // light amber — produced, in-progress
    static readonly Color32 DownColor     = new Color32(150, 55, 40, 255);  // dark red-brown — consumed (down)
    static readonly Color32 DownLiveColor = new Color32(196, 102, 80, 255); // light red-brown — consumed, in-progress

    // Labels used in hover tooltips. Set by the owner; sensible defaults otherwise.
    string _unitLabel = "points";
    string _upLabel   = "up";
    string _downLabel = "down";

    // ── State ───────────────────────────────────────────────────────────────
    float[] _up    = Array.Empty<float>();  // oldest → newest; fills the rightmost slots
    float[] _down;                           // null = single-sided
    int     _slots = 1;                      // total columns (fixed bar width = plot / slots)
    bool    _lastIsLive;
    float   _maxV;                           // top of the visible range (per direction)

    // Plot geometry from the last Redraw — reused by hover hit-testing so the cursor
    // maps to exactly the column it sits over.
    int _x0, _pw;

    RawImage      _image;
    RectTransform _rt;
    Texture2D     _tex;
    Color32[]     _px;
    int           _w, _h;

    public int   BarCount => _up.Length;
    public float MaxValue => _maxV;
    public bool  HasData  => _maxV > 0f && BarCount > 0;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    void Awake() {
        _rt    = (RectTransform)transform;
        _image = GetComponent<RawImage>();
        _image.color         = Color.white; // RawImage tints the texture; white = no tint
        _image.raycastTarget = true;        // needed for per-bar hover tooltips
    }

    void OnRectTransformDimensionsChange() {
        if (_rt != null && _image != null) Redraw();
    }

    void OnDestroy() {
        if (_tex != null) Destroy(_tex);
    }

    // ── Public API ──────────────────────────────────────────────────────────

    // Tooltip text config. unit e.g. "food points"; upLabel/downLabel e.g. "produced"/"eaten".
    public void SetLabels(string unit, string upLabel, string downLabel) {
        _unitLabel = string.IsNullOrEmpty(unit) ? "points" : unit;
        _upLabel   = upLabel   ?? "";
        _downLabel = downLabel ?? "";
    }

    // Feeds new series, oldest → newest. `down` may be null for a single-sided chart.
    // slotCount is the fixed number of columns (bar width = plot width / slotCount);
    // values fill the rightmost slots so bars grow in from the right. lastIsLive tints
    // the newest bars as an in-progress period.
    public void SetData(float[] up, float[] down, int slotCount, bool lastIsLive = false) {
        _slots = Mathf.Max(1, slotCount);
        _up    = Window(up);
        _down  = down != null ? Window(down) : null;
        _lastIsLive = lastIsLive;
        RecomputeScale();
        Redraw();
    }

    // Keeps only the newest `_slots` values (caller normally windows already; guards anyway).
    float[] Window(float[] a) {
        if (a == null) return Array.Empty<float>();
        if (a.Length <= _slots) return a;
        var trimmed = new float[_slots];
        Array.Copy(a, a.Length - _slots, trimmed, 0, _slots);
        return trimmed;
    }

    // ── Scaling ─────────────────────────────────────────────────────────────

    void RecomputeScale() {
        float hi = 0f;
        for (int i = 0; i < _up.Length; i++)   if (_up[i]   > hi) hi = _up[i];
        if (_down != null) for (int i = 0; i < _down.Length; i++) if (_down[i] > hi) hi = _down[i];
        _maxV = hi > 0f ? hi : 1f;
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    bool EnsureTexture() {
        if (_rt == null || _image == null) return false;
        int w = Mathf.RoundToInt(_rt.rect.width);
        int h = Mathf.RoundToInt(_rt.rect.height);
        if (w < 4 || h < 4) return false; // not laid out yet
        if (_tex == null || _w != w || _h != h) {
            if (_tex != null) Destroy(_tex);
            _w = w; _h = h;
            _tex = new Texture2D(w, h, TextureFormat.RGBA32, false) {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp,
            };
            _px = new Color32[w * h];
            _image.texture = _tex;
        }
        return true;
    }

    void Redraw() {
        if (!EnsureTexture()) return;
        FillAll(BgColor);

        bool twoSided = _down != null;
        // Baseline: centered for a diverging chart, bottom for single-sided.
        int baseY = twoSided ? _h / 2 : (AxisThickness + 1);
        int upRoom   = _h - baseY - 1;
        int downRoom = twoSided ? (baseY - (AxisThickness + 1)) : 0;
        // Shared half-height so equal values read as equal pixel heights up vs down.
        int half = twoSided ? Mathf.Max(1, Mathf.Min(upRoom, downRoom)) : Mathf.Max(1, upRoom);

        DrawAxes(baseY); // Y axis (left) + horizontal baseline at baseY

        _x0 = AxisThickness + 1;
        _pw = Mathf.Max(1, _w - _x0);

        float ymax  = Mathf.Max(1e-3f, _maxV * (1f + RangePadFrac));
        float slotW = (float)_pw / _slots;
        int   barW  = Mathf.Max(1, Mathf.FloorToInt(slotW) - BarGap);

        // Each series is right-aligned INDEPENDENTLY (newest in the rightmost slot), so
        // series of different lengths still line up by day — e.g. a shorter consumed
        // history (added later than produced) starts further right and shares "today"
        // with production, rather than being misaligned to production's oldest day.
        DrawBars(_up, slotW, barW, ymax, half, baseY, true);            // produced — up
        if (twoSided) DrawBars(_down, slotW, barW, ymax, half, baseY, false); // consumed — down

        _tex.SetPixels32(_px);
        _tex.Apply(false);
    }

    // Draws one series' bars, right-aligned into the rightmost slots. `up` chooses
    // direction (above the baseline) and colour; the newest entry is tinted live.
    void DrawBars(float[] vals, float slotW, int barW, float ymax, int half, int baseY, bool up) {
        int n = vals.Length;
        int firstSlot = _slots - n;
        for (int i = 0; i < n; i++) {
            float v = vals[i];
            if (v <= 0f) continue;
            int slot = firstSlot + i;
            int bx = _x0 + Mathf.RoundToInt(slot * slotW);
            int bh = Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp01(v / ymax) * half));
            bool live = _lastIsLive && i == n - 1;
            if (up) FillRect(bx, baseY + 1,  barW, bh, live ? UpLiveColor   : UpColor);
            else    FillRect(bx, baseY - bh, barW, bh, live ? DownLiveColor : DownColor);
        }
    }

    // Y axis up the left edge + a horizontal baseline (zero line) at baseY.
    void DrawAxes(int baseY) {
        if (_w < 2 || _h < 2) return;
        for (int t = 0; t < AxisThickness; t++) {
            for (int y = 0; y < _h; y++) Plot(t, y, AxisColor);          // Y axis — left
            for (int x = 0; x < _w; x++) Plot(x, baseY + t, AxisColor);  // baseline
        }
    }

    // ── Hover tooltips ──────────────────────────────────────────────────────

    public void OnPointerMove(PointerEventData eventData) {
        int slot = SlotAtScreenPoint(eventData);
        if (slot < 0 || slot >= _slots) { TooltipSystem.Hide(); return; }

        // Map the slot into each series independently (both right-aligned). The rightmost
        // slot is the newest day, so the day label comes from the slot, not an array index.
        int upIdx   = slot - (_slots - _up.Length);
        int downN   = _down != null ? _down.Length : 0;
        int downIdx = slot - (_slots - downN);
        bool hasUp   = upIdx   >= 0 && upIdx   < _up.Length;
        bool hasDown = _down != null && downIdx >= 0 && downIdx < downN;
        if (!hasUp && !hasDown) { TooltipSystem.Hide(); return; }

        int daysAgo = (_slots - 1) - slot;                  // rightmost slot = newest
        string title = daysAgo == 0 ? "today"
                     : daysAgo == 1 ? "yesterday"
                     : daysAgo + " days ago";

        string body = "";
        if (hasUp)   body  = _upLabel + " " + Mathf.RoundToInt(_up[upIdx]) + " " + _unitLabel;
        if (hasDown) body += (body.Length > 0 ? "\n" : "") + _downLabel + " " + Mathf.RoundToInt(_down[downIdx]) + " " + _unitLabel;
        TooltipSystem.Show(title, body);
    }

    public void OnPointerExit(PointerEventData eventData) {
        TooltipSystem.Hide();
    }

    // Maps a screen-space pointer to a 0-based column using the same geometry Redraw
    // used, so the hit column matches the drawn bars exactly.
    int SlotAtScreenPoint(PointerEventData eventData) {
        var canvas = GetComponentInParent<Canvas>();
        Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? canvas.worldCamera : null;
        Vector2 local;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rt, eventData.position, cam, out local))
            return -1;
        float pixelX = local.x - _rt.rect.xMin; // texture pixel 0 sits at the rect's left edge
        float slotW  = (float)_pw / _slots;
        if (slotW <= 0f) return -1;
        return Mathf.FloorToInt((pixelX - _x0) / slotW);
    }

    // ── Pixel primitives ──────────────────────────────────────────────────────

    void FillAll(Color32 c) {
        for (int i = 0; i < _px.Length; i++) _px[i] = c;
    }

    void FillRect(int x, int y, int w, int h, Color32 c) {
        for (int oy = 0; oy < h; oy++)
            for (int ox = 0; ox < w; ox++)
                Plot(x + ox, y + oy, c);
    }

    void Plot(int x, int y, Color32 c) {
        if (x < 0 || x >= _w || y < 0 || y >= _h) return;
        _px[y * _w + x] = c;
    }
}
