using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// PriceGraphPanel — the price-graph widget inside the TradingPanel.
//
// The UI (wood-frame background, the graph, the Bid/Ask series toggles, the
// Hour/Day/Week range buttons, the corner price labels) lives as scene
// GameObjects under this component's GameObject and is wired in via the
// [SerializeField] references below — so layout is tweaked in the editor, not
// in code. This component only drives behaviour: feeding history / live prices
// into the graph, the series toggles, and the range selection.
//
// TradingPanel feeds it via SetHistory() / SetLivePrice() and reads RangeSec /
// BucketSec when it queries the server.

public class PriceGraphPanel : MonoBehaviour {
    // ── Scene references (wired in the inspector) ───────────────────────
    [SerializeField] PriceGraph      _graph;
    [SerializeField] TextMeshProUGUI _titleLabel;
    [SerializeField] TextMeshProUGUI _maxLabel;
    [SerializeField] TextMeshProUGUI _minLabel;
    [SerializeField] TextMeshProUGUI _xStartLabel;
    [SerializeField] TextMeshProUGUI _xMidLabel;
    [SerializeField] TextMeshProUGUI _xEndLabel;
    [SerializeField] Button          _bidBtn;
    [SerializeField] Button          _askBtn;
    [SerializeField] Button          _hourBtn;
    [SerializeField] Button          _dayBtn;
    [SerializeField] Button          _weekBtn;

    // Bid = blue, Ask = red — matches the line colours in PriceGraph.
    static readonly Color ColorBidActive   = new Color(0.20f, 0.55f, 1.00f);
    static readonly Color ColorAskActive   = new Color(1.00f, 0.35f, 0.35f);
    static readonly Color ColorInactive    = new Color(0.80f, 0.80f, 0.80f);
    static readonly Color ColorRangeActive = new Color(1.00f, 0.80f, 0.35f); // warm gold

    // Range presets: span in seconds, downsample bucket in seconds.
    const int HourRange = 3600,   HourBucket = 60;
    const int DayRange  = 86400,  DayBucket  = 600;
    const int WeekRange = 604800, WeekBucket = 3600;

    bool _showBid;
    bool _showAsk;

    // Selected range — Hour by default. TradingPanel reads these when querying.
    int _rangeSec  = HourRange;
    int _bucketSec = HourBucket;
    public int RangeSec  => _rangeSec;
    public int BucketSec => _bucketSec;

    // Logged history (for the selected range) + a live tip. The graph is built
    // from history plus one extra point for the current live bid/ask, so the
    // right end of the line tracks the market between server log ticks.
    string           _item;
    PriceHistoryData _history;
    int              _liveBid, _liveAsk;
    bool             _hasLive;

    void Awake() {
        if (_graph == null || _titleLabel == null || _maxLabel == null
                || _minLabel == null || _bidBtn == null || _askBtn == null
                || _hourBtn == null || _dayBtn == null || _weekBtn == null
                || _xStartLabel == null || _xMidLabel == null || _xEndLabel == null) {
            Debug.LogError("PriceGraphPanel: scene references not wired — check the inspector.");
            return;
        }
        _bidBtn.onClick.AddListener(() => ToggleSeries(true));
        _askBtn.onClick.AddListener(() => ToggleSeries(false));
        _hourBtn.onClick.AddListener(() => SelectRange(HourRange, HourBucket));
        _dayBtn.onClick.AddListener(()  => SelectRange(DayRange,  DayBucket));
        _weekBtn.onClick.AddListener(() => SelectRange(WeekRange, WeekBucket));
        RecolorRangeButtons(); // Hour active by default
    }

    // ── Public API ──────────────────────────────────────────────────────

    // Feeds a logged price-history response into the graph. Null / empty safe.
    public void SetHistory(PriceHistoryData data) {
        EnsureItem(data != null ? data.item : null);
        _history = data;
        Rebuild();
    }

    // Feeds the current live bid/ask (from the order book) as the graph's last
    // point, so the line's right end tracks the market in real time between
    // the server's minute-cadence log samples.
    public void SetLivePrice(string item, int bid, int ask) {
        EnsureItem(item);
        _liveBid = bid;
        _liveAsk = ask;
        _hasLive = bid > 0 || ask > 0;
        Rebuild();
    }

    // ── Range selection ─────────────────────────────────────────────────

    void SelectRange(int rangeSec, int bucketSec) {
        _rangeSec  = rangeSec;
        _bucketSec = bucketSec;
        RecolorRangeButtons();
        TradingPanel.instance?.RequeryHistory(); // pull fresh data for the new range
    }

    void RecolorRangeButtons() {
        _hourBtn.image.color = _rangeSec == HourRange ? ColorRangeActive : ColorInactive;
        _dayBtn.image.color  = _rangeSec == DayRange  ? ColorRangeActive : ColorInactive;
        _weekBtn.image.color = _rangeSec == WeekRange ? ColorRangeActive : ColorInactive;
    }

    // ── Internals ───────────────────────────────────────────────────────

    // Drops cached history / live tip when the viewed item changes, so stale
    // data from the previous item can't leak in before fresh responses arrive.
    void EnsureItem(string item) {
        if (item == _item) return;
        _item    = item;
        _history = null;
        _hasLive = false;
    }

    // Rebuilds the graph from the response's history plus, when available, one
    // extra point carrying the live bid/ask. Needs the response's time window —
    // with no response yet there is nothing to plot against, so it draws empty.
    void Rebuild() {
        _titleLabel.text = string.IsNullOrEmpty(_item) ? "" : _item;

        if (_history == null) {
            _graph.SetData(Array.Empty<long>(), Array.Empty<int>(), Array.Empty<int>(),
                           0, 0, _bucketSec, false);
            RefreshLabels();
            RefreshXLabels();
            return;
        }

        PriceSample[] s = _history.samples;
        int n     = s != null ? s.Length : 0;
        int extra = _hasLive ? 1 : 0;
        long[] times = new long[n + extra];
        int[]  bid   = new int[n + extra];
        int[]  ask   = new int[n + extra];
        for (int i = 0; i < n; i++) {
            times[i] = s[i].t;
            bid[i]   = s[i].bid;
            ask[i]   = s[i].ask;
        }
        if (_hasLive) {
            times[n] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bid[n]   = _liveBid;
            ask[n]   = _liveAsk;
        }
        _graph.SetData(times, bid, ask,
                       _history.startSec, _history.endSec, _history.bucketSec, _hasLive);
        RefreshLabels();
        RefreshXLabels();
    }

    void ToggleSeries(bool bid) {
        if (bid) _showBid = !_showBid;
        else     _showAsk = !_showAsk;
        _graph.SetSeriesVisible(_showBid, _showAsk);
        _bidBtn.image.color = _showBid ? ColorBidActive : ColorInactive;
        _askBtn.image.color = _showAsk ? ColorAskActive : ColorInactive;
        RefreshLabels(); // Y range — and thus the max label — tracks visible series
    }

    void RefreshLabels() {
        if (_graph.SampleCount == 0 || !_graph.HasData) {
            _maxLabel.text = "";
            _minLabel.text = "";
            return;
        }
        _maxLabel.text = FormatPrice(_graph.MaxPrice);
        _minLabel.text = FormatPrice(_graph.MinPrice); // axis floor — always 0
    }

    // fen → liang for display, matching the price formatting elsewhere in
    // TradingPanel (price / 100f).
    static string FormatPrice(int fen) {
        return (fen / 100f).ToString("0.##");
    }

    // Sets the three X-axis labels to relative times across the visible window:
    // start = the full span back, mid = halfway, end = "now".
    void RefreshXLabels() {
        if (_history == null || _history.endSec <= _history.startSec) {
            _xStartLabel.text = "";
            _xMidLabel.text   = "";
            _xEndLabel.text   = "";
            return;
        }
        long span = _history.endSec - _history.startSec;
        _xStartLabel.text = RelLabel(span);
        _xMidLabel.text   = RelLabel(span / 2);
        _xEndLabel.text   = RelLabel(0);
    }

    // secondsAgo → "now" / "30m" / "6h" / "3d".
    static string RelLabel(long secondsAgo) {
        if (secondsAgo < 45)     return "now";
        if (secondsAgo < 3600)   return Mathf.RoundToInt(secondsAgo / 60f)   + "m";
        if (secondsAgo < 172800) return Mathf.RoundToInt(secondsAgo / 3600f) + "h";
        return Mathf.RoundToInt(secondsAgo / 86400f) + "d";
    }
}
