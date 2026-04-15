using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Rounds the preferred height of a TMP text element up to the nearest
// integer pixel. Attach alongside TextMeshProUGUI so that a parent
// VerticalLayoutGroup positions subsequent elements at integer offsets.
// Uses TMP's own text metrics (not LayoutUtility) to avoid recursive
// layout recalculation.
[RequireComponent(typeof(TMP_Text))]
public class PixelAlignedText : MonoBehaviour, ILayoutElement {
    TMP_Text text;

    void Awake() {
        text = GetComponent<TMP_Text>();
    }

    public int layoutPriority => 10;

    public float minWidth       => -1;
    public float preferredWidth => -1;
    public float flexibleWidth  => -1;
    public float minHeight      => -1;
    public float flexibleHeight => -1;

    public float preferredHeight {
        get {
            if (text == null) return -1;
            float width = text.rectTransform.rect.width;
            if (width <= 0) return -1;
            float h = text.GetPreferredValues(text.text, width, 0).y;
            return Mathf.Ceil(h);
        }
    }

    public void CalculateLayoutInputHorizontal() { }
    public void CalculateLayoutInputVertical() { }
}
