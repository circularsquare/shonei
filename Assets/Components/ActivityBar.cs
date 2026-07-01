using UnityEngine;
using UnityEngine.UI;

// Compact horizontal bar split into the five ActivityGroup buckets — each segment's width is that
// group's recent-time fraction (ActivityTracker.Fraction). Segments are code-built once and
// re-proportioned via SetFractions, mirroring InventoryBar's pattern.
//
// Anchor-fraction layout (like InventoryBar): each segment fills [cum, cum+frac] of the track via
// anchorMin.x / anchorMax.x — no pixel math, no HorizontalLayoutGroup. Visibility uses
// Image.enabled (a disabled Graphic doesn't raycast) so a zero-width segment's Tooltippable can't
// fire — never SetActive, which trips Tooltippable.OnDisable→Hide (see
// reference_tooltippable_setactive_hides).
[RequireComponent(typeof(RectTransform))]
public class ActivityBar : MonoBehaviour {
    // Muted palette, one per ActivityGroup (index = (int)ActivityGroup). Matches SPEC-mcp guidance.
    static readonly Color[] GroupColors = {
        new Color(0.29f, 0.49f, 0.73f), // Working — blue   (deeper/more saturated)
        new Color(0.48f, 0.70f, 0.48f), // Walking — green  (swapped with working)
        new Color(0.54f, 0.37f, 0.70f), // Leisure — purple (deeper/more saturated)
        new Color(0.83f, 0.53f, 0.22f), // Idle    — orange (deeper/more saturated)
        new Color(0.19f, 0.23f, 0.42f), // Sleep   — navy
    };
    static readonly string[] GroupNames = { "working", "walking", "leisure", "idle", "sleep" };
    const float Eps = 0.005f; // hide sub-0.5% slivers

    // Legend source — shared with SwatchLegend so the legend can't drift from the bars.
    public static LegendEntry[] LegendEntries() {
        var arr = new LegendEntry[GroupColors.Length];
        for (int i = 0; i < arr.Length; i++) arr[i] = new LegendEntry(GroupNames[i], GroupColors[i]);
        return arr;
    }

    RectTransform track;
    Image[] segs;
    Tooltippable[] tips;

    void EnsureBuilt() {
        if (segs != null) return;
        track = GetComponent<RectTransform>();
        int n = ActivityTracker.Count;
        segs = new Image[n];
        tips = new Tooltippable[n];
        for (int i = 0; i < n; i++) {
            var go = new GameObject("seg_" + GroupNames[i], typeof(RectTransform));
            go.transform.SetParent(track, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 0.5f); rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = GroupColors[i];
            segs[i] = img;
            tips[i] = go.AddComponent<Tooltippable>();
        }
    }

    // Re-proportion the segments from the mouse's recency-weighted activity. Zero-width groups are
    // hidden (no sliver, no tooltip). Safe to call every refresh tick.
    public void SetFractions(ActivityTracker activity) {
        EnsureBuilt();
        float cum = 0f;
        for (int i = 0; i < segs.Length; i++) {
            float frac = activity.Fraction((ActivityGroup)i);
            if (frac > Eps) {
                var rt = segs[i].rectTransform;
                rt.anchorMin = new Vector2(Mathf.Clamp01(cum), 0);
                rt.anchorMax = new Vector2(Mathf.Clamp01(cum + frac), 1);
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                segs[i].enabled = true;
                // SetLiveBody (not .body=) so a tooltip already open on this segment refreshes
                // in place as the % drifts under a lingering pointer.
                tips[i].SetLiveBody(GroupNames[i] + " " + Mathf.RoundToInt(frac * 100f) + "%");
            } else {
                segs[i].enabled = false;
            }
            cum += frac;
        }
    }
}
