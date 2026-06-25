using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Horizontal distribution bar for one item in GlobalInventoryPanel. A fixed-width track holds
// colored segments showing where the item lives — storage (green), floor (yellow), mice/carried
// (gray), market (blue), installed=reservoir fuel + furnishings (brown), elsewhere (orange).
//
// Width semantics (the clever bit):
//   target >  owned : the whole bar represents the TARGET. Segments fill the owned portion; the
//                     shortfall (owned..target) shows as a dark dull-red deficit zone; the target
//                     marker sits at the right end.
//   target <= owned : the whole bar represents OWNED. Segments fill the whole bar; the target
//                     marker sits at target/owned.
// In both cases the marker fraction is target/scale (scale = max(owned, target)).
//
// The target marker is DRAGGABLE (leaf rows only): drag it left/right and the target updates on
// RELEASE to fraction × scale — so dragging it off the right edge multiplies the target. It lives
// OUTSIDE the masked track (sibling, parented to the bar cell) so it can overhang the pill and
// receive drags without being clipped. The marker is a wide transparent HIT area (easy to grab)
// with a thin red LINE child; on leaf rows the line overhangs the pill ~1px top+bottom, on group
// rows it sits flush (a visual cue that it isn't draggable).
//
// A second, dark-GREEN marker shows the STORAGE CEILING — how much of this item the player's
// storage could hold if topped off (storage already held + free space in stacks that currently
// allow it). It's read-only: a full-pill-height line, no drag handle. Fraction =
// ceiling/scale, so like the target it pins to the right edge once the ceiling exceeds the scale.
//
// Segments/marker carry Tooltippables. Built in code (dynamic per-item content); GameObjects are
// created once and reused. Segment visibility is driven by `Image.enabled` (a disabled Graphic
// doesn't raycast) — NOT SetActive / disabling the Tooltippable, either of which trips
// Tooltippable.OnDisable→TooltipSystem.Hide and kills an active tooltip (see SPEC
// reference_tooltippable_setactive_hides).
public class InventoryBar : MonoBehaviour {
    [SerializeField] RectTransform track;   // masked pill; segments anchor-fill horizontally within it
    [SerializeField] Sprite segmentSprite;  // optional rounded sprite for segments; null = plain rect (clipped by track's Mask)

    // Set by InventoryDetailRow for LEAF rows: commit a dragged target (fen). Null ⇒ not draggable
    // (group rows — their target is a read-only sum of leaf targets).
    [System.NonSerialized] public System.Action<int> onTargetSet;

    // Muted palette (matches the SPEC-mcp UI colour guidance).
    static readonly Color cStorage   = new Color(0.48f, 0.70f, 0.48f); // green
    static readonly Color cFloor     = new Color(0.85f, 0.72f, 0.32f); // yellow
    static readonly Color cMice      = new Color(0.55f, 0.55f, 0.55f); // gray
    static readonly Color cMarket    = new Color(0.42f, 0.56f, 0.74f); // blue
    static readonly Color cInstalled = new Color(0.50f, 0.36f, 0.22f); // brown (reservoir fuel / furnishings)
    static readonly Color cOther     = new Color(0.82f, 0.52f, 0.28f); // orange
    static readonly Color cDeficit   = new Color(0.42f, 0.20f, 0.20f); // dark dull red
    static readonly Color cMarker    = new Color(0.58f, 0.12f, 0.12f); // dark red (target)
    static readonly Color cCapacity  = new Color(0.16f, 0.34f, 0.18f); // dark green (storage-capacity ceiling)

    const int   BucketCount     = 6;             // storage, floor, mice, market, installed, other
    const int   SegCount        = BucketCount + 1; // + deficit zone
    const float MarkerLineWidthPx = 2f;          // visible red line
    const float MarkerHitWidthPx  = 14f;         // invisible grab zone around the line
    const float MarkerOverhangPx  = 2f;          // leaf marker: line taller than the pill (1px above + 1px below) — the draggable cue
    const float CapacityHitWidthPx  = 10f;       // grab zone around the capacity line (hover → tooltip)
    const float CapacityLineWidthPx = 2f;        // same width as the target line; the two differ by height (target overhangs taller)
    const float CapacityHeightFrac  = 1f;        // capacity line spans the full pill height (target line overhangs slightly taller)
    const int   TargetSnapFen     = 10;          // dragged targets snap to 0.1 liang
    const float Eps               = 0.0005f;

    Image[]        segs;     // 0..5 buckets, 6 = deficit
    Tooltippable[] segTips;
    RectTransform  markerHit;   // wide transparent grab zone (raycast + drag + tooltip)
    Image          markerHitImg;
    Image          markerLine;  // thin red visible line
    Tooltippable   markerTip;
    RectTransform  capHit;      // capacity marker grab zone (raycast + tooltip; NOT draggable)
    Image          capHitImg;
    Image          capLine;     // thin dark-green visible line, shorter than the pill
    Tooltippable   capTip;

    // Captured at SetData for the drag math (target/owned don't change mid-drag).
    Item  lastItem;
    int   lastScale;
    bool  dragging;
    float dragScale;

    void EnsureBuilt() {
        if (segs != null) return;
        if (track == null) track = GetComponent<RectTransform>();
        segs    = new Image[SegCount];
        segTips = new Tooltippable[SegCount];
        for (int i = 0; i < SegCount; i++) {
            var go = new GameObject("seg" + i, typeof(RectTransform));
            go.transform.SetParent(track, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 0.5f); rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            if (segmentSprite != null) { img.sprite = segmentSprite; img.type = Image.Type.Sliced; }
            segs[i]    = img;
            segTips[i] = go.AddComponent<Tooltippable>();
        }
        // Marker hit zone — parented to the bar cell (NOT the masked track) so it can overhang and
        // receive drags. track fills the bar cell, so their geometry matches.
        var hgo = new GameObject("marker", typeof(RectTransform));
        hgo.transform.SetParent(transform, false);
        markerHit = hgo.GetComponent<RectTransform>();
        markerHit.anchorMin = new Vector2(0, 0); markerHit.anchorMax = new Vector2(0, 1); markerHit.pivot = new Vector2(0.5f, 0.5f);
        markerHit.sizeDelta = new Vector2(MarkerHitWidthPx, 0f); // grab zone = pill height (the line's height is set separately)
        markerHitImg = hgo.AddComponent<Image>();
        markerHitImg.color = new Color(0, 0, 0, 0); markerHitImg.raycastTarget = true; // transparent grab zone
        markerTip = hgo.AddComponent<Tooltippable>();
        hgo.AddComponent<BarTargetDragHandle>().bar = this;
        // Thin red line, centered in the hit zone.
        var lgo = new GameObject("line", typeof(RectTransform));
        lgo.transform.SetParent(hgo.transform, false);
        var lrt = lgo.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0.5f, 0); lrt.anchorMax = new Vector2(0.5f, 1); lrt.pivot = new Vector2(0.5f, 0.5f);
        lrt.sizeDelta = new Vector2(MarkerLineWidthPx, 0);
        markerLine = lgo.AddComponent<Image>();
        markerLine.color = cMarker; markerLine.raycastTarget = false;

        // Capacity marker — mirrors the target marker (hit zone + line + tooltip) but is NOT
        // draggable (no drag handle) and the line is shorter than the pill, signalling read-only.
        // Also parented to the bar cell so it tracks the same geometry as the track.
        var cgo = new GameObject("capacity", typeof(RectTransform));
        cgo.transform.SetParent(transform, false);
        capHit = cgo.GetComponent<RectTransform>();
        capHit.anchorMin = new Vector2(0, 0); capHit.anchorMax = new Vector2(0, 1); capHit.pivot = new Vector2(0.5f, 0.5f);
        capHit.sizeDelta = new Vector2(CapacityHitWidthPx, 0f);
        capHitImg = cgo.AddComponent<Image>();
        capHitImg.color = new Color(0, 0, 0, 0); capHitImg.raycastTarget = true; // transparent grab zone
        capTip = cgo.AddComponent<Tooltippable>();
        // Full-pill-height line, centered in the hit zone. Same width as the target line; the
        // slightly-overhanging draggable target line reads as taller.
        var clgo = new GameObject("line", typeof(RectTransform));
        clgo.transform.SetParent(cgo.transform, false);
        var clrt = clgo.GetComponent<RectTransform>();
        float pad = (1f - CapacityHeightFrac) * 0.5f;
        clrt.anchorMin = new Vector2(0.5f, pad); clrt.anchorMax = new Vector2(0.5f, 1f - pad); clrt.pivot = new Vector2(0.5f, 0.5f);
        clrt.sizeDelta = new Vector2(CapacityLineWidthPx, 0);
        capLine = clgo.AddComponent<Image>();
        capLine.color = cCapacity; capLine.raycastTarget = false;
    }

    // All quantities in fen; rendered via ItemStack.FormatQ for tooltips. `installed` = reservoir
    // fuel + furnishings equipped in buildings (shown only on the bar, no dedicated column).
    // `capacity` = free storage space that currently allows this item (empty allowed stacks + room
    // in stacks already holding it); the dark-green capacity marker sits at the storage CEILING
    // (storage + capacity), i.e. how much this item's storage could hold if topped off.
    public void SetData(Item item, int storage, int floor, int mice, int market, int installed, int total, int target, int capacity) {
        EnsureBuilt();
        lastItem = item;
        int owned = total;
        int other = Mathf.Max(0, total - storage - floor - mice - market - installed); // remainder: blueprint/processor buffers, in transit
        int scale = Mathf.Max(owned, target);
        lastScale = scale;

        if (scale <= 0) { for (int i = 0; i < SegCount; i++) HideSeg(i); HideMarker(); HideCapacity(); return; }

        int[]    vals  = { storage, floor, mice, market, installed, other };
        Color[]  cols  = { cStorage, cFloor, cMice, cMarket, cInstalled, cOther };
        string[] names = { "storage", "floor", "mice", "market", "installed", "elsewhere" };

        float cum = 0f;
        for (int i = 0; i < BucketCount; i++) {
            float frac = vals[i] / (float)scale;
            if (frac > Eps) SetSeg(i, cum, cum + frac, cols[i], ItemStack.FormatQ(vals[i], item) + " in " + names[i]);
            else            HideSeg(i);
            cum += frac;
        }

        // Deficit zone: only when we own less than target.
        if (target > owned)
            SetSeg(BucketCount, cum, 1f, cDeficit,
                   ItemStack.FormatQ(target - owned, item) + " below target " + ItemStack.FormatQ(target, item));
        else
            HideSeg(BucketCount);

        // Target marker: always shown (fraction target/scale → right end when target ≥ owned).
        // Skipped while dragging so the live drag position isn't clobbered by the refresh tick.
        if (!dragging) ShowMarker(target / (float)scale, "target " + ItemStack.FormatQ(target, item));

        // Capacity marker: dark-green line at the storage ceiling (storage already held + free
        // space that allows the item). Hidden when no storage allows the item at all.
        int ceiling = storage + capacity;
        if (ceiling > 0) ShowCapacity(ceiling / (float)scale, "storage " + ItemStack.FormatQ(ceiling, item) + " (" + ItemStack.FormatQ(capacity, item) + " free)");
        else             HideCapacity();
    }

    void SetSeg(int i, float start, float end, Color c, string tip) {
        var rt = segs[i].rectTransform;
        rt.anchorMin = new Vector2(Mathf.Clamp01(start), 0);
        rt.anchorMax = new Vector2(Mathf.Clamp01(end),   1);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        segs[i].color   = c;
        segs[i].enabled = true;        // gates raycast → tooltip; never toggle SetActive/Tooltippable
        segTips[i].body = tip;
    }
    void HideSeg(int i) {
        segs[i].enabled = false;       // disabled Graphic = invisible AND non-raycast (no tooltip)
    }

    void ShowMarker(float frac, string tip) {
        bool draggable = onTargetSet != null;
        PositionMarkerX(Mathf.Clamp01(frac));
        // Leaf rows: line overhangs the pill (taller) — the draggable cue. Group rows: line is flush
        // with the pill (no overhang) to signal it's read-only.
        markerLine.rectTransform.sizeDelta = new Vector2(MarkerLineWidthPx, draggable ? MarkerOverhangPx : 0f);
        markerLine.enabled = true;
        markerHitImg.raycastTarget = true;
        markerTip.body = tip;
        markerHit.SetAsLastSibling(); // draw above the segments/track
    }
    void HideMarker() {
        if (markerLine != null) markerLine.enabled = false;
        if (markerHitImg != null) markerHitImg.raycastTarget = false;
    }

    void ShowCapacity(float frac, string tip) {
        float f = Mathf.Clamp01(frac);
        capHit.anchorMin = new Vector2(f, 0); capHit.anchorMax = new Vector2(f, 1);
        capHit.anchoredPosition = Vector2.zero;
        capLine.enabled = true;
        capHitImg.raycastTarget = true;
        capTip.body = tip;
        // Both markers sit above the track. Keep the draggable target marker on TOP so its grab zone
        // wins where the two overlap (capHit's transparent hit area would otherwise block the drag).
        if (markerHit != null) markerHit.SetAsLastSibling();
    }
    void HideCapacity() {
        if (capLine != null) capLine.enabled = false;
        if (capHitImg != null) capHitImg.raycastTarget = false;
    }

    // Places the marker hit zone at horizontal fraction `f` of the bar (f may exceed 1 mid-drag).
    void PositionMarkerX(float f) {
        markerHit.anchorMin = new Vector2(f, 0); markerHit.anchorMax = new Vector2(f, 1);
        markerHit.anchoredPosition = Vector2.zero;
    }

    // ── Drag-to-set-target (leaf rows) ──────────────────────────────────
    // Mapping: target = fraction × scale, where fraction is the cursor's x position across the bar
    // (0 at left, 1 at right, >1 past the right edge) and scale is what the bar represented when the
    // drag began. Commit happens on release only.
    public void OnMarkerBeginDrag(PointerEventData e) {
        if (onTargetSet == null) return; // not editable (group row)
        dragging = true;
        dragScale = lastScale;
    }
    public void OnMarkerDrag(PointerEventData e) {
        if (!dragging) return;
        float f = Mathf.Max(0f, FractionFromScreen(e));
        PositionMarkerX(f); // unclamped ≥0 — can travel past the right edge (clipped by the scroll viewport)
        TooltipSystem.Show("", "target " + ItemStack.FormatQ(DragFen(f), lastItem)); // live preview
    }
    public void OnMarkerEndDrag(PointerEventData e) {
        if (!dragging) return;
        dragging = false;
        float f = Mathf.Max(0f, FractionFromScreen(e));
        onTargetSet(DragFen(f)); // commit; the resulting Refresh repositions the marker
    }

    int DragFen(float fraction) {
        int raw = Mathf.RoundToInt(fraction * dragScale);
        return Mathf.Max(0, Mathf.RoundToInt(raw / (float)TargetSnapFen) * TargetSnapFen);
    }

    float FractionFromScreen(PointerEventData e) {
        Vector2 local;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(track, e.position, e.pressEventCamera, out local);
        float w = track.rect.width;
        return w > 0f ? (local.x - track.rect.xMin) / w : 0f;
    }
}

// Forwards drag events from the (wide, transparent) marker hit zone to its owning InventoryBar.
// Separate component because the events must land on the marker GameObject the pointer hits.
public class BarTargetDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler {
    [System.NonSerialized] public InventoryBar bar;
    public void OnBeginDrag(PointerEventData e) { if (bar != null) bar.OnMarkerBeginDrag(e); }
    public void OnDrag(PointerEventData e)      { if (bar != null) bar.OnMarkerDrag(e); }
    public void OnEndDrag(PointerEventData e)   { if (bar != null) bar.OnMarkerEndDrag(e); }
}
