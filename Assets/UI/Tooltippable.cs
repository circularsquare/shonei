using UnityEngine;
using UnityEngine.EventSystems;

// Add this component to any UI element to give it a tooltip on hover.
// Set title and body in code or in the Inspector.
// The tooltip is rendered by TooltipSystem (must be present in the scene).
public class Tooltippable : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler {
    public string title;
    [TextArea(2, 6)] public string body;

    // True while the pointer is over this element. Lets callers live-update the tooltip
    // (SetLiveBody) so a long hover over content that's changing underneath stays fresh.
    bool hovered;

    public void OnPointerEnter(PointerEventData eventData) {
        hovered = true;
        TooltipSystem.Show(title, body);
    }

    public void OnPointerExit(PointerEventData eventData) {
        hovered = false;
        TooltipSystem.Hide();
        // Unity only fires OnPointerEnter on the parent once — so when the pointer
        // moves from a nested child Tooltippable back onto its parent (still hovered),
        // nothing re-shows the parent's tooltip. Walk up and restore it here.
        // If the pointer is actually leaving the parent too, the parent's own
        // OnPointerExit will fire right after and hide it again.
        Transform t = transform.parent;
        while (t != null) {
            var parentTip = t.GetComponent<Tooltippable>();
            if (parentTip != null && parentTip.isActiveAndEnabled) {
                parentTip.hovered = true;
                TooltipSystem.Show(parentTip.title, parentTip.body);
                return;
            }
            t = t.parent;
        }
    }

    void OnDisable() {
        hovered = false;
        TooltipSystem.Hide();
    }

    // Update the tooltip body, re-showing immediately if this element is currently hovered.
    // For live content (e.g. the activity bar's per-segment %) whose value drifts while the
    // pointer lingers — the caller's existing refresh tick pushes the new text in.
    public void SetLiveBody(string newBody) {
        body = newBody;
        if (hovered) TooltipSystem.Show(title, body);
    }
}
