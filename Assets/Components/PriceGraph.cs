using System;
using UnityEngine;
using UnityEngine.UI;

// PriceGraph — a price-history line graph rendered into a Texture2D and shown
// through a RawImage.
//
// It draws up to three series: the mid price (always) plus optional bid and
// ask lines. Data is fed as parallel arrays of unix-second timestamps and fen
// prices, plus the time window [startSec, endSec] and the downsample bucket;
// the mid is derived here so the display rule lives in one place (Mid).
//
// X axis is real time: each sample sits at its timestamp within the window, so
// server-downtime gaps render as blank stretches. Two samples connect only if
// they are within ~1.5 buckets of each other — a wider gap breaks the line.
// The Y axis starts at 0.
//
// A texture (not a custom Graphic mesh) is used because it renders reliably as
// a runtime-added component and suits the game's pixel-art look — point
// filtering keeps the plot crisp.

[RequireComponent(typeof(RawImage))]
public class PriceGraph : MonoBehaviour {
    // ── Tuning ──────────────────────────────────────────────────────────
    const int   AxisThickness = 1;    // px — X/Y axis line width
    const int   LineThickness = 2;    // px
    const int   DotSize       = 3;    // px — isolated (unconnectable) samples
    const float RangePadFrac  = 0.1f; // vertical headroom above the data

    // The plot fills the entire RawImage rect: the X axis runs along the
    // bottom edge, the Y axis up the right edge, and samples plot into the
    // remaining area. Size the Graph rect in the inspector to match the
    // visible plot region — labels, buttons and the wood-frame background sit
    // as separate siblings around it.

    static readonly Color32 BgColor   = new Color32(0, 0, 0, 0);        // transparent — wood frame shows through
    static readonly Color32 AxisColor = new Color32(74, 58, 40, 255);   // dark brown — X/Y axis lines
    static readonly Color32 MidColor  = new Color32(125, 90, 0, 255);   // dark amber
    static readonly Color32 BidColor  = new Color32(28, 72, 158, 255);  // dark blue (buy side)
    static readonly Color32 AskColor  = new Color32(165, 38, 38, 255);  // dark red  (sell side)

    // ── State ───────────────────────────────────────────────────────────
    long[] _times = Array.Empty<long>();
    int[]  _bid   = Array.Empty<int>();
    int[]  _ask   = Array.Empty<int>();
    long   _startSec, _endSec; // time window — axis left/right edges
    long   _bucketSec;         // downsample bucket width (drives gap detection)
    bool   _lastIsLive;        // last sample is the live tip → never gap before it
    bool   _showBid;
    bool   _showAsk;
    int    _maxP;              // top of the visible price range, fen (axis floor is 0)

    RawImage      _image;
    RectTransform _rt;
    Texture2D     _tex;
    Color32[]     _px;
    int           _w, _h;

    public int  SampleCount => _times.Length;
    public int  MinPrice    => 0;       // axis always starts at 0
    public int  MaxPrice    => _maxP;
    public bool ShowBid     => _showBid;
    public bool ShowAsk     => _showAsk;
    public bool HasData     => _maxP > 0 && SampleCount > 0;

    // ── Mid-price rule ──────────────────────────────────────────────────
    // The one place the display rule lives. Prices are fen; 0 = no order on
    // that side. Both sides → arithmetic mid; bid only → 2× bid; ask only →
    // ½ ask; neither → 0 (treated as "no point").
    public static int Mid(int bid, int ask) {
        bool hasBid = bid > 0, hasAsk = ask > 0;
        if (hasBid && hasAsk) return (bid + ask) / 2;
        if (hasBid)           return bid * 2;
        if (hasAsk)           return ask / 2;
        return 0;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────

    void Awake() {
        _rt    = (RectTransform)transform;
        _image = GetComponent<RawImage>();
        _image.color         = Color.white; // RawImage tints the texture; white = no tint
        _image.raycastTarget = false;       // display-only
    }

    // The rect drives the texture size — redraw (and resize) when it changes.
    void OnRectTransformDimensionsChange() {
        if (_rt != null && _image != null) Redraw();
    }

    void OnDestroy() {
        if (_tex != null) Destroy(_tex);
    }

    // ── Public API ──────────────────────────────────────────────────────

    // Feeds new history. times/bid/ask are parallel (one entry per sample).
    // startSec/endSec are the time window (axis edges); bucketSec is the
    // downsample width; lastIsLive marks the final sample as the live tip.
    // Passing null arrays clears the graph.
    public void SetData(long[] times, int[] bid, int[] ask,
                        long startSec, long endSec, long bucketSec, bool lastIsLive) {
        if (times == null || bid == null || ask == null) {
            _times = Array.Empty<long>();
            _bid   = Array.Empty<int>();
            _ask   = Array.Empty<int>();
        } else if (times.Length != bid.Length || times.Length != ask.Length) {
            Debug.LogError($"PriceGraph.SetData: array length mismatch " +
                           $"({times.Length}/{bid.Length}/{ask.Length})");
            _times = Array.Empty<long>();
            _bid   = Array.Empty<int>();
            _ask   = Array.Empty<int>();
        } else {
            _times = times; _bid = bid; _ask = ask;
        }
        _startSec   = startSec;
        _endSec     = endSec;
        _bucketSec  = bucketSec;
        _lastIsLive = lastIsLive;
        RecomputeScale();
        Redraw();
    }

    public void SetSeriesVisible(bool showBid, bool showAsk) {
        _showBid = showBid;
        _showAsk = showAsk;
        RecomputeScale(); // Y range tracks only the visible series
        Redraw();
    }

    // ── Scaling ─────────────────────────────────────────────────────────

    // Top of the Y range = max value across visible series. The floor is 0.
    void RecomputeScale() {
        int n = SampleCount;
        int hi = 0;
        for (int i = 0; i < n; i++) {
            int m = Mid(_bid[i], _ask[i]);
            if (m > 0)                   hi = Mathf.Max(hi, m);
            if (_showBid && _bid[i] > 0) hi = Mathf.Max(hi, _bid[i]);
            if (_showAsk && _ask[i] > 0) hi = Mathf.Max(hi, _ask[i]);
        }
        _maxP = hi > 0 ? hi : 1;
    }

    // ── Rendering ───────────────────────────────────────────────────────

    // (Re)allocates the backing texture to match the current rect size.
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
        DrawAxes();

        int n = SampleCount;
        if (n >= 1 && _endSec > _startSec) {
            // Plot data region — fills the rect, inset on the bottom/right
            // just enough to sit inside the X/Y axes.
            int x0 = 0;
            int y0 = AxisThickness + 1;
            int pw = Mathf.Max(1, (_w - AxisThickness - 1) - x0);
            int ph = Mathf.Max(1, _h - y0);

            // X: each sample at its real time within the [_startSec, _endSec]
            // window. Clamped — a snapped first bucket can fall just before the
            // window, and a live tip just after it.
            double span = _endSec - _startSec;
            int[] xs = new int[n];
            for (int i = 0; i < n; i++) {
                float f = Mathf.Clamp01((float)((_times[i] - _startSec) / span));
                xs[i] = x0 + Mathf.RoundToInt(f * (pw - 1));
            }

            // Gap: break the line where two samples are more than ~1.5 buckets
            // apart (a missing bucket / downtime). The segment into a live tip
            // is exempt — it is continuous with "now".
            bool[] gapBefore = new bool[n];
            float gapThreshold = _bucketSec * 1.5f;
            for (int i = 1; i < n; i++) {
                bool liveSeg = _lastIsLive && i == n - 1;
                gapBefore[i] = !liveSeg && _bucketSec > 0
                               && (_times[i] - _times[i - 1]) > gapThreshold;
            }

            float ymax = Mathf.Max(1e-3f, _maxP * (1f + RangePadFrac));

            // Bid/ask first so the always-on mid line draws on top.
            if (_showBid) DrawSeries(xs, gapBefore, i => _bid[i], ymax, y0, ph, BidColor);
            if (_showAsk) DrawSeries(xs, gapBefore, i => _ask[i], ymax, y0, ph, AskColor);
            DrawSeries(xs, gapBefore, i => Mid(_bid[i], _ask[i]), ymax, y0, ph, MidColor);
        }

        _tex.SetPixels32(_px);
        _tex.Apply(false);
    }

    // Draws the axes: a horizontal X axis along the bottom edge and a
    // vertical Y axis up the right edge, meeting at the bottom-right corner.
    // The plot data is inset by AxisThickness (see Redraw) so it sits inside
    // them.
    void DrawAxes() {
        if (_w < 2 || _h < 2) return;
        for (int t = 0; t < AxisThickness; t++) {
            for (int x = 0; x < _w; x++)     Plot(x,             t, AxisColor); // X axis — bottom
            for (int y = 0; y < _h; y++)     Plot(_w - 1 - t,    y, AxisColor); // Y axis — right
        }
    }

    // Draws one series as a polyline. Two samples connect only when both have
    // a value and no gap sits between them. A sample that can't connect to
    // either neighbour is drawn as a dot.
    void DrawSeries(int[] xs, bool[] gapBefore, Func<int, int> valueAt,
                    float ymax, int y0, int ph, Color32 col) {
        int n = xs.Length;
        for (int i = 0; i < n; i++) {
            int v = valueAt(i);
            if (v <= 0) continue;
            bool connectsLeft  = i > 0     && valueAt(i - 1) > 0 && !gapBefore[i];
            bool connectsRight = i < n - 1 && valueAt(i + 1) > 0 && !gapBefore[i + 1];
            if (connectsLeft) {
                DrawLine(xs[i - 1], YOf(valueAt(i - 1), ymax, y0, ph),
                         xs[i],     YOf(v,              ymax, y0, ph), col);
            }
            if (!connectsLeft && !connectsRight) {
                DrawBlock(xs[i], YOf(v, ymax, y0, ph), DotSize, col);
            }
        }
    }

    static int YOf(int v, float ymax, int y0, int ph) {
        return y0 + Mathf.RoundToInt(Mathf.Clamp01(v / ymax) * (ph - 1));
    }

    // ── Pixel primitives ────────────────────────────────────────────────

    void FillAll(Color32 c) {
        for (int i = 0; i < _px.Length; i++) _px[i] = c;
    }

    void Plot(int x, int y, Color32 c) {
        if (x < 0 || x >= _w || y < 0 || y >= _h) return;
        _px[y * _w + x] = c;
    }

    // Bresenham line, drawn LineThickness px wide.
    void DrawLine(int x0, int y0, int x1, int y1, Color32 c) {
        int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true) {
            for (int ox = 0; ox < LineThickness; ox++)
                for (int oy = 0; oy < LineThickness; oy++)
                    Plot(x0 + ox, y0 + oy, c);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 <  dx) { err += dx; y0 += sy; }
        }
    }

    void DrawBlock(int cx, int cy, int size, Color32 c) {
        int h = size / 2;
        for (int ox = -h; ox <= h; ox++)
            for (int oy = -h; oy <= h; oy++)
                Plot(cx + ox, cy + oy, c);
    }
}
