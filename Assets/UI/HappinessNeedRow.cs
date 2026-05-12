using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One row in the GlobalHappinessPanel needs table.
//
// Prefab setup:
//   Root: HorizontalLayoutGroup (child force expand width = false, spacing = 8)
//     NeedName     — TextMeshProUGUI, LayoutElement preferred width ~110
//     AvgHappiness — TextMeshProUGUI, LayoutElement preferred width ~40  (e.g. "0.6", or "+2.0" for temp)
//     FillBar      — FillBar prefab,  LayoutElement preferred width 30 (scales with point value)
//     Satisfaction — TextMeshProUGUI, LayoutElement preferred width ~50  (raw satisfaction avg, debug)
public class HappinessNeedRow : MonoBehaviour {
    [SerializeField] TextMeshProUGUI needNameText;
    [SerializeField] TextMeshProUGUI avgHappinessText; // avg happiness contribution (0.0–max points)
    [SerializeField] FillBar         fillBar;
    [SerializeField] TextMeshProUGUI satisfactionText; // raw satisfaction avg (0.0–5.0), debug

    const float BarWidthPerPoint = 30f; // pixels per happiness point the field is worth

    LayoutElement fillBarLayout;

    void Awake() {
        fillBarLayout = fillBar.GetComponent<LayoutElement>();
    }

    public void SetNeedName(string name) {
        needNameText.text = name;
    }

    // For value-based needs (wheat, fruit, soymilk, fountain, social).
    public void Refresh(int satisfied, int total, float avgSatisfaction) {
        avgHappinessText.text = ((float)satisfied / total).ToString("0.0");
        SetBar((float)satisfied / total, points: 1f);
        satisfactionText.text = avgSatisfaction.ToString("0.0");
    }

    // For boolean needs (housing) — fraction satisfied as a 0.0–1.0 decimal.
    public void RefreshBool(int satisfied, int total) {
        avgHappinessText.text = ((float)satisfied / total).ToString("0.0");
        SetBar((float)satisfied / total, points: 1f);
        satisfactionText.text = "";
    }

    // For temperature — worth 2 points max; bar fills to 100% at avgTempScore == 2.0.
    public void RefreshTemp(float avgTempScore) {
        avgHappinessText.text = avgTempScore.ToString("0.0");
        SetBar(Mathf.Clamp01(avgTempScore / 2f), points: 2f);
        satisfactionText.text = "";
    }

    // For open-ended additive scores (furnishing). Bar scales to `maxScore` so its
    // width reflects the ceiling for the current colony (e.g. 1 point per furnishing
    // slot type × best item). maxScore of 0 yields an empty bar.
    public void RefreshScore(float avgScore, float maxScore) {
        avgHappinessText.text = avgScore.ToString("0.0");
        float fill = maxScore > 0f ? Mathf.Clamp01(avgScore / maxScore) : 0f;
        SetBar(fill, points: Mathf.Max(1f, maxScore));
        satisfactionText.text = "";
    }

    // Sets fill and resizes the bar proportional to the field's happiness point value.
    void SetBar(float fill, float points) {
        if (fillBarLayout != null) fillBarLayout.preferredWidth = BarWidthPerPoint * points;
        fillBar.SetFill(fill);
        fillBar.gameObject.SetActive(true);
    }
}
