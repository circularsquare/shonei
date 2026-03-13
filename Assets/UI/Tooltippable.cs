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
    }

    void OnDisable() {
        TooltipSystem.Hide();
    }
}
