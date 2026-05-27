using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One row in the GlobalHappinessPanel needs table.
//
// Design contract:
//   Every row uses the same two-step API — Configure once at spawn, Refresh every tick.
//
//   Configure(name, points) sets the row identity AND sizes the bar
//   (BarWidthPerPoint × points). The row stores `maxPoints`; per-refresh callers can't
//   accidentally violate the bar-width invariant.
//
//   Refresh(averagePoints, detailText, tooltipBody) is the only per-tick entry point.
//   averagePoints is in [0, maxPoints] and drives both the middle text and the bar fill.
//   detailText is the optional raw underlying value shown to the right; pass "" for
//   rows that don't expose one. tooltipBody is the hover-tooltip body for this row
//   (live-updated each refresh so the displayed values are current); pass "" to disable.
//
// Prefab setup:
//   Root: HorizontalLayoutGroup (child force expand width = false, spacing = 8)
//     NeedName     — TextMeshProUGUI, LayoutElement preferred width ~110
//     AvgHappiness — TextMeshProUGUI, LayoutElement preferred width ~40
//     FillBar      — FillBar prefab,  LayoutElement preferred width is set at runtime by Configure
//     Satisfaction — TextMeshProUGUI, LayoutElement preferred width ~50  (raw debug value, optional)
public class HappinessNeedRow : MonoBehaviour {
    [SerializeField] TextMeshProUGUI needNameText;
    [SerializeField] TextMeshProUGUI avgHappinessText; // avg happiness contribution (0.0–maxPoints)
    [SerializeField] FillBar         fillBar;
    [SerializeField] TextMeshProUGUI satisfactionText; // raw debug value, optional

    const float BarWidthPerPoint = 30f; // pixels per happiness point the field is worth

    LayoutElement fillBarLayout;
    float maxPoints = 1f; // set in Configure; guard against div-by-zero in Refresh
    Tooltippable tooltippable;

    void Awake() {
        fillBarLayout = fillBar.GetComponent<LayoutElement>();
    }

    // Initialises both the row identity and the bar width in a single call so the
    // BarWidthPerPoint × points invariant is impossible to violate at refresh time.
    // Call once at spawn. `points` must be > 0 — 1 for value/bool needs, 2 for temperature,
    // Db.maxFurnishingPerMouse for furnishing, AnimalController.MaxFoodStorageBonus for food storage.
    public void Configure(string name, float points) {
        needNameText.text = name;
        maxPoints = points > 0f ? points : 1f;
        if (fillBarLayout != null) fillBarLayout.preferredWidth = BarWidthPerPoint * maxPoints;
        fillBar.gameObject.SetActive(true);

        // Hover tooltip — added dynamically rather than expected on the prefab, so adding
        // a new row type doesn't require prefab edits. Title is fixed at the row name;
        // body is live-updated each Refresh so the numbers are current at hover time.
        if (tooltippable == null) tooltippable = gameObject.GetComponent<Tooltippable>()
                                              ?? gameObject.AddComponent<Tooltippable>();
        tooltippable.title = name;
        tooltippable.body  = "";
    }

    // The single per-tick refresh entry point used by every row.
    // averagePoints: this row's average happiness contribution (0..maxPoints). Shown in
    //                the middle text and used as bar fill (averagePoints / maxPoints).
    // detailText:    optional raw underlying value shown to the right (e.g. raw satisfaction
    //                avg for value needs). Pass "" for rows with no underlying value.
    // tooltipBody:   hover-tooltip body. Live-updated; pass "" to leave the tooltip empty.
    public void Refresh(float averagePoints, string detailText = "", string tooltipBody = "") {
        avgHappinessText.text = averagePoints.ToString("0.0");
        fillBar.SetFill(Mathf.Clamp01(averagePoints / maxPoints));
        satisfactionText.text = detailText;
        if (tooltippable != null) tooltippable.body = tooltipBody;
    }
}
