using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One legend entry: a label + its swatch color. `line` swatches render as a thin vertical bar
// (for marker-style legend items like the inventory target/capacity lines); otherwise a square.
public struct LegendEntry {
    public readonly string label;
    public readonly Color  color;
    public readonly bool   line;
    public LegendEntry(string label, Color color, bool line = false) {
        this.label = label; this.color = color; this.line = line;
    }
}

// A one-row legend: swatch + label per entry, pulled from a bar's `LegendEntries()` so the legend
// can never drift from the bar colors. Self-builds on Awake into its own HorizontalLayoutGroup.
// Shared by the population panel (ActivityBar) and the inventory panel (InventoryBar) — pick the
// source in the inspector. Labels are born in TMP's default font and re-stamped by
// UITextRuntimeStyle at runtime, like other code-built UI text.
[RequireComponent(typeof(RectTransform))]
public class SwatchLegend : MonoBehaviour {
    public enum Source { ActivityBar, InventoryBar }
    [SerializeField] Source source;

    const float SquareSize = 9f;
    const float LineWidth  = 2f;
    const float LineHeight = 11f;

    bool built;
    void Awake() { Build(); }

    void Build() {
        if (built) return;
        built = true;

        var hlg = GetComponent<HorizontalLayoutGroup>();
        if (hlg == null) hlg = gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8; hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
        hlg.childAlignment = TextAnchor.MiddleLeft;

        LegendEntry[] entries = source == Source.ActivityBar
            ? ActivityBar.LegendEntries()
            : InventoryBar.LegendEntries();
        foreach (var e in entries) AddEntry(e);
    }

    void AddEntry(LegendEntry e) {
        var entry = new GameObject("leg_" + e.label, typeof(RectTransform));
        entry.transform.SetParent(transform, false);
        var ehlg = entry.AddComponent<HorizontalLayoutGroup>();
        ehlg.spacing = 3; ehlg.childControlWidth = true; ehlg.childControlHeight = true;
        ehlg.childForceExpandWidth = false; ehlg.childForceExpandHeight = false;
        ehlg.childAlignment = TextAnchor.MiddleLeft;

        var sw = new GameObject("sw", typeof(RectTransform));
        sw.transform.SetParent(entry.transform, false);
        var img = sw.AddComponent<Image>();
        img.color = e.color; img.raycastTarget = false;
        var swle = sw.AddComponent<LayoutElement>();
        swle.preferredWidth  = e.line ? LineWidth  : SquareSize;
        swle.minWidth        = swle.preferredWidth;
        swle.preferredHeight = e.line ? LineHeight : SquareSize;
        swle.flexibleWidth   = 0;

        var lbl = new GameObject("lbl", typeof(RectTransform));
        lbl.transform.SetParent(entry.transform, false);
        var tmp = lbl.AddComponent<TextMeshProUGUI>();
        tmp.text = e.label; tmp.fontSize = 16; tmp.color = Color.black;
        tmp.alignment = TextAlignmentOptions.BottomLeft;
        tmp.enableWordWrapping = false; tmp.raycastTarget = false;
        lbl.AddComponent<LayoutElement>().preferredHeight = 14;
    }
}
