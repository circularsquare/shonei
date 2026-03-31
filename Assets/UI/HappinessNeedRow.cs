using UnityEngine;
using TMPro;

/// <summary>
/// One row in the GlobalHappinessPanel needs table.
///
/// Prefab setup:
///   Root: HorizontalLayoutGroup (child force expand width = false, spacing = 8)
///     NeedName  — TextMeshProUGUI, LayoutElement preferred width ~110
///     Count     — TextMeshProUGUI, LayoutElement preferred width ~40  (e.g. "4/5")
///     FillBar   — FillBar prefab,  LayoutElement preferred width ~80
///     AvgValue  — TextMeshProUGUI, LayoutElement preferred width ~50
/// </summary>
public class HappinessNeedRow : MonoBehaviour {
    [SerializeField] TextMeshProUGUI needNameText;
    [SerializeField] TextMeshProUGUI countText;
    [SerializeField] FillBar         fillBar;
    [SerializeField] TextMeshProUGUI avgValueText;

    public void SetNeedName(string name) {
        needNameText.text = name;
    }

    // For value-based needs (wheat, fruit, soymilk, fountain, social).
    public void Refresh(int satisfied, int total, float avgVal) {
        countText.text    = $"{satisfied}/{total}";
        fillBar.SetFill((float)satisfied / total);
        fillBar.gameObject.SetActive(true);
        avgValueText.text = avgVal.ToString("0.0");
    }

    // For boolean needs (housing) — no meaningful raw average, just a count.
    public void RefreshBool(int satisfied, int total) {
        countText.text    = $"{satisfied}/{total}";
        fillBar.SetFill((float)satisfied / total);
        fillBar.gameObject.SetActive(true);
        avgValueText.text = "--";
    }

    // For temperature — different semantics, no per-animal threshold, so hide the bar.
    public void RefreshTemp(float avgTempScore) {
        countText.text    = "--";
        fillBar.gameObject.SetActive(false);
        avgValueText.text = (avgTempScore >= 0f ? "+" : "") + avgTempScore.ToString("0.0");
    }
}
