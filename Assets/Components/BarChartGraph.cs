using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// BarChartGraph — a generic historic bar chart rendered into a Texture2D and shown
// through a RawImage. Reusable for any per-period series (food per day, research per
// day, …); feed it value arrays via SetData (simple) or SetSeries (stacked).
//
// Single- or double-sided:
//   • up bars rise from a baseline; down bars (optional) drop below it on a SHARED
//     scale so equal values read as equal heights.
//
// Stacked segments: each side can hold MULTIPLE segments that stack within one bar
// (e.g. food eaten + food decayed as two colours of the downward bar; scientist +
// passive research as two colours of the upward bar). A bar's height is the sum of
// its segments; the scale is driven by the tallest stacked total across all slots.
//
// Fixed-width slots: the plot is divided into `slotCount` equal columns and bars fill
// in FROM THE RIGHT (newest on the right), so every bar is the same width regardless
// of how many days of data exist yet. Each segment is right-aligned INDEPENDENTLY, so
// a segment with a shorter history (added later) still lines up by day.
//
// Hovering a day-column shows a tooltip listing each segment's value via the shared
// TooltipSystem. The optional `lastIsLive` flag tints the newest bars lighter to mark
// an in-progress period and labels its tooltip "today".
//
// A texture (not a custom Graphic mesh) is used for the same reasons as PriceGraph:
// it renders reliably as a runtime-added component and suits the pixel-art look —
// point filtering keeps the bars crisp.
[RequireComponent(typeof(RawImage))]
public class BarChartGraph : MonoBehaviour, IPointerMoveHandler, IPointerExitHandler {
    // One stacked segment of a bar: per-day values (oldest → newest), the colour to draw
    // it, the lighter tint for the in-progress (newest) day, and a tooltip label.
    public struct Segment {
        public float[] values;
        public Color32 color;
        public Color32 liveColor;
        public string  label;

        public Segment(float[] values, Color32 color, Color32 liveColor, string label) {
            this.values = values ?? Array.Empty<float>();
            this.color = color;
            this.liveColor = liveColor;
            this.label = label ?? "";
        }
    }

    // ── Shared palette ──────────────────────────────────────────────────────
    // Public so panels can build Segments that match the chart's earthy pixel-art look
    // without redeclaring colours (avoids drift). Each colour has a lighter "live" tint
    // for the in-progress day.
    public static readonly Color32 Amber     = new Color32(125, 90,  0,  255); // dark amber — produced (up)
    public static readonly Color32 AmberLive = new Color32(181, 138, 54, 255);
    public static readonly Color32 Red       = new Color32(150, 55,  40, 255); // dark red-brown — consumed / decay (down)
    public static readonly Color32 RedLive    = new Color32(196, 102, 80, 255);
    public static readonly Color32 Slate      = new Color32(96,  88,  72, 255); // grey-brown — spoilage / waste
    public static readonly Color32 SlateLive   = new Color32(140, 130, 108, 255);
    public static readonly Color32 Blue       = new Color32(60,  110, 170, 255); // research — scientist work
    public static readonly Color32 BlueLive    = new Color32(104, 158, 214, 255);
    public static readonly Color32 Green       = new Color32(90,  140, 70,  255); // research — passive gain
    public static readonly Color32 GreenLive   = new Color32(138, 186, 110, 255);

    // ── Tuning ──────────────────────────────────────────────────────────────
    const int   AxisThickness = 1;     // px — axis / baseline line width
    const int   BarGap        = 1;     // px — gap between adjacent bar columns
    const float RangePadFrac  = 0.1f;  // headroom beyond the largest bar

    static readonly Color32 BgColor   = new Color32(0, 0, 0, 0);        // transparent — wood frame shows through
    static readonly Color32 AxisColor = new Color32(74, 58, 40, 255);   // dark brown — axes / baseline

    // ── State ───────────────────────────────────────────────────────────────
    Segment[] _up   = Array.Empty<Segment>();  // stacked from the baseline outward
    Segment[] _down = Array.Empty<Segment>();
    bool      _twoSided;
    int       _slots = 1;                       // total columns (fixed bar width = plot / slots)
    bool      _lastIsLive;
    float     _maxV;                            // top of the visible range (per direction)
    int       _barCount;                        // longest segment length, for HasData

    // Plot geometry from the last Redraw — reused by hover hit-testing so the cursor
    // maps to exactly the column it sits over.
    int _x0, _pw;

    RawImage      _image;
    RectTransform _rt;
    Texture2D     _tex;
    Color32[]     _px;
    int           _w, _h;

    public int   BarCount => _barCount;
    public float MaxValue => _maxV;
    public bool  HasData  => _maxV > 0f && _barCount > 0;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    void Awake() {
        _rt    = (RectTransform)transform;
        _image = GetComponent<RawImage>();
        _image.color         = Color.white; // RawImage tints the texture; white = no tint
        _image.raycastTarget = true;        // needed for per-bar hover tooltips
        // Hidden until the first successful Redraw assigns a texture — otherwise the RawImage
        // shows its default white box for a frame (before layout/data) on first open.
        _image.enabled = false;
    }

    void OnRectTransformDimensionsChange() {
        if (_rt != null && _image != null) Redraw();
    }

    void OnDestroy() {
        if (_tex != null) Destroy(_tex);
    }

    // ── Public API ──────────────────────────────────────────────────────────

    // Simple single-segment-per-side API. `down` may be null for a single-sided chart.
    // Uses the shared amber (up) / red (down) palette. Per-segment tooltip labels are
    // generic here ("up"/"down"); callers wanting named segments use SetSeries directly.
    public void SetData(float[] up, float[] down, int slotCount, bool lastIsLive = false) {
        var upSeg   = new[] { new Segment(up, Amber, AmberLive, "up") };
        Segment[] downSeg = down != null
            ? new[] { new Segment(down, Red, RedLive, "down") }
            : null;
        SetSeries(upSeg, downSeg, slotCount, lastIsLive);
    }

    // Stacked API. `up`/`down` are arrays of segments stacked from the baseline outward;
    // pass null/empty `down` for a single-sided chart. slotCount is the fixed column count
    // (bar width = plot width / slotCount); values fill the rightmost slots. lastIsLive
    // tints the newest bars as an in-progress period.
    public void SetSeries(Segment[] up, Segment[] down, int slotCount, bool lastIsLive = false) {
        _slots      = Mathf.Max(1, slotCount);
        _up         = NormalizeSegments(up);
        _down       = NormalizeSegments(down);
        _twoSided   = _down.Length > 0;
        _lastIsLive = lastIsLive;
        RecomputeScale();
        Redraw();
    }

    // Windows each segment to the newest `_slots` values (callers normally window already).
    Segment[] NormalizeSegments(Segment[] segs) {
        if (segs == null || segs.Length == 0) return Array.Empty<Segment>();
        var result = new Segment[segs.Length];
        for (int i = 0; i < segs.Length; i++) {
            var s = segs[i];
            result[i] = new Segment(Window(s.values), s.color, s.liveColor, s.label);
        }
        return result;
    }

    float[] Window(float[] a) {
        if (a == null) return Array.Empty<float>();
        if (a.Length <= _slots) return a;
        var trimmed = new float[_slots];
        Array.Copy(a, a.Length - _slots, trimmed, 0, _slots);
        return trimmed;
    }

    // ── Scaling ─────────────────────────────────────────────────────────────

    void RecomputeScale() {
        _barCount = 0;
        foreach (var s in _up)   if (s.values.Length > _barCount) _barCount = s.values.Length;
        foreach (var s in _down) if (s.values.Length > _barCount) _barCount = s.values.Length;
        // Scale is driven by the tallest STACKED total across all slots, shared up vs down.
        float hi = Mathf.Max(StackedMax(_up), StackedMax(_down));
        _maxV = hi > 0f ? hi : 1f;
    }

    // Largest per-slot sum of a side's segments (each segment right-aligned independently).
    float StackedMax(Segment[] segs) {
        float hi = 0f;
        for (int slot = 0; slot < _slots; slot++) {
            float sum = 0f;
            foreach (var s in segs) sum += Mathf.Max(0f, ValueAt(s, slot));
            if (sum > hi) hi = sum;
        }
        return hi;
    }

    // A segment's value at a slot, right-aligned: the newest value sits in the rightmost
    // slot. Slots before the segment's history begins read 0.
    static float ValueAt(Segment s, int slot, int slots) {
        int idx = slot - (slots - s.values.Length);
        return (idx >= 0 && idx < s.values.Length) ? s.values[idx] : 0f;
    }
    float ValueAt(Segment s, int slot) => ValueAt(s, slot, _slots);

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

        // Baseline: centered for a diverging chart, bottom for single-sided.
        int baseY = _twoSided ? _h / 2 : (AxisThickness + 1);
        int upRoom   = _h - baseY - 1;
        int downRoom = _twoSided ? (baseY - (AxisThickness + 1)) : 0;
        // Shared half-height so equal values read as equal pixel heights up vs down.
        int half = _twoSided ? Mathf.Max(1, Mathf.Min(upRoom, downRoom)) : Mathf.Max(1, upRoom);

        DrawAxes(baseY); // Y axis (left) + horizontal baseline at baseY

        _x0 = AxisThickness + 1;
        _pw = Mathf.Max(1, _w - _x0);

        float ymax  = Mathf.Max(1e-3f, _maxV * (1f + RangePadFrac));
        float slotW = (float)_pw / _slots;
        int   barW  = Mathf.Max(1, Mathf.FloorToInt(slotW) - BarGap);

        DrawStacks(_up,   slotW, barW, ymax, half, baseY, true);
        if (_twoSided) DrawStacks(_down, slotW, barW, ymax, half, baseY, false);

        _tex.SetPixels32(_px);
        _tex.Apply(false);
        _image.enabled = true; // real texture is ready — safe to show (no white flash)
    }

    // Draws one side's stacked bars. For each slot, segments stack from the baseline
    // outward in array order. Heights are computed from cumulative totals (not summed
    // per-segment) so rounding never opens a gap between adjacent segments.
    void DrawStacks(Segment[] segs, float slotW, int barW, float ymax, int half, int baseY, bool up) {
        for (int slot = 0; slot < _slots; slot++) {
            int   bx       = _x0 + Mathf.RoundToInt(slot * slotW);
            bool  live     = _lastIsLive && slot == _slots - 1;
            int   accumPx  = 0;     // pixels already filled from the baseline
            float accumVal = 0f;    // running stacked value
            foreach (var s in segs) {
                float v = ValueAt(s, slot);
                if (v <= 0f) continue;
                accumVal += v;
                int topPx = Mathf.RoundToInt(Mathf.Clamp01(accumVal / ymax) * half);
                int segH  = Mathf.Max(1, topPx - accumPx);
                Color32 c = live ? s.liveColor : s.color;
                if (up) FillRect(bx, baseY + 1 + accumPx,        barW, segH, c);
                else    FillRect(bx, baseY - accumPx - segH,     barW, segH, c);
                accumPx = topPx;
            }
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

        int daysAgo = (_slots - 1) - slot;                  // rightmost slot = newest
        string title = daysAgo == 0 ? "today"
                     : daysAgo == 1 ? "yesterday"
                     : daysAgo + " days ago";

        string body = "";
        body = AppendSegmentLines(body, _up,   slot, up: true);
        body = AppendSegmentLines(body, _down, slot, up: false);
        if (body.Length == 0) { TooltipSystem.Hide(); return; }
        TooltipSystem.Show(title, body);
    }

    // Appends one "label ±value" line per segment with a positive value at `slot`. The sign
    // encodes direction (up = +, down = −) since both sides share the diverging chart.
    string AppendSegmentLines(string body, Segment[] segs, int slot, bool up) {
        string sign = up ? "+" : "-";
        foreach (var s in segs) {
            float v = ValueAt(s, slot);
            if (v <= 0f) continue;
            string line = (string.IsNullOrEmpty(s.label) ? "" : s.label + " ")
                        + sign + FormatValue(v);
            body += (body.Length > 0 ? "\n" : "") + line;
        }
        return body;
    }

    // Small magnitudes (e.g. per-day research gains) read better at tenths precision; larger
    // ones (food points in the hundreds) are cleaner as integers.
    static string FormatValue(float v) {
        return v >= 10f ? Mathf.RoundToInt(v).ToString() : v.ToString("0.0");
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
