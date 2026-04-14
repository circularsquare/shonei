using UnityEngine;
using UnityEngine.EventSystems;

// Add this component to any UI element to give it a tooltip on hover.
// Set title and body in code or in the Inspector.
// The tooltip is rendered by TooltipSystem (must be present in the scene).
public class Tooltippable : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler {
    public string title;
    [TextArea(2, 6)] public string body;

    public void OnPointerEnter(PointerEventData eventData) {
        TooltipSystem.Show(title, body);
    }

    public void OnPointerExit(PointerEventData eventData) {
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
                TooltipSystem.Show(parentTip.title, parentTip.body);
                return;
            }
            t = t.parent;
        }
    }

    void OnDisable() {
        TooltipSystem.Hide();
    }
}
