using UnityEngine;
using UnityEngine.UI;

// Canonical helper for revealing UI without the "min-height pop" — where a panel
// opens at padding-only height for one frame, then snaps to full size the next.
//
// Why the pop happens: when you SetActive(true) a subtree (or spawn rows into it),
// Unity only schedules a layout rebuild for end-of-frame. Worse, when the subtree
// nests LayoutGroups/ContentSizeFitters (content > group > card > section > row),
// a single top-down ForceRebuildLayoutImmediate uses each child's *stale* size when
// sizing its parent fitter — so it can take several frames to settle.
//
// The fix is two parts, both required:
//   1. Canvas.ForceUpdateCanvases() once, so any dirtied TMP regenerates and reports
//      current preferred sizes (text-driven heights are 0/stale before this).
//   2. Rebuild BOTTOM-UP: every child is sized before the parent whose fitter measures
//      it, so the whole subtree settles in this frame.
//
// Usage: after toggling visibility / spawning content, call
//   LayoutUtil.RebuildImmediate(outermostRectThatChangesSize);
// Pass the outermost rect whose size depends on the change (e.g. the scroll Content),
// not just the row you toggled — its ancestors' fitters need to re-measure too.
public static class LayoutUtil {
    public static void RebuildImmediate(RectTransform root) {
        if (root == null) return;
        Canvas.ForceUpdateCanvases();
        RebuildBottomUp(root);
    }

    // Depth-first: rebuild a rect only after its (active) children are rebuilt, so a
    // ContentSizeFitter reads already-correct child sizes. ForceRebuildLayoutImmediate
    // also recurses internally — the redundancy is cheap and the explicit bottom-up
    // order is what guarantees nested fitters resolve in a single pass.
    static void RebuildBottomUp(RectTransform rt) {
        for (int i = 0; i < rt.childCount; i++) {
            if (rt.GetChild(i) is RectTransform child && child.gameObject.activeInHierarchy)
                RebuildBottomUp(child);
        }
        // Only rects that actually drive layout (a LayoutGroup or ContentSizeFitter,
        // both ILayoutController) need a rebuild; plain rects are skipped.
        if (rt.GetComponent<ILayoutController>() != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
    }
}
