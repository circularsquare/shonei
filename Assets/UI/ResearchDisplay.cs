using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Attach to the ResearchDisplay prefab.
// Each card shows: icon, cost text, and a progress bar (0 → 2×cost) with a threshold
// marker at cost. Clicking the card toggles "study".
//
// The progress bar is a FillBar instance using a single Sliced sprite
// (Misc/progressbarresearch) clipped by RectMask2D.
//
// Prefab hierarchy (set up in editor):
//   Background        — Image, tinted by state
//   Icon              — Image, preserveAspect
//   Cost              — TextMeshProUGUI, shows "0.0 / 5"
//   FillBar           — FillBar prefab instance (mask mode, bar sprite inside)
//   ThresholdMarker   — Image, thin vertical line at the midpoint (unlock threshold)
//   ButtonStudy       — Button spanning the full card (transparent overlay)
//   Tooltippable      — component on root for hover info
public class ResearchDisplay : MonoBehaviour {
    [Header("Prefab Refs")]
    public Image            background;
    public Image            icon;
    public TextMeshProUGUI  cost;
    public Button           buttonStudy;
    public FillBar          progressBar;
    public Image            thresholdMarker;

    const string SpritePath     = "Sprites/Researches/";
    const string SpriteFallback = "Sprites/Researches/default";

    ResearchNodeData _node;
    ResearchSystem   _rs;

    // Lightweight refresh — updates progress bar, cost text, and background tint.
    public void RefreshProgress() {
        if (_node == null || _rs == null) return;
        bool unlocked          = _rs.IsUnlocked(_node.id);
        bool canStudy          = _rs.CanStudy(_node);
        bool isStudied         = _rs.IsStudied(_node.id);
        bool activelyStudied   = _rs.IsActivelyResearched(_node.id);
        float p                = _rs.GetProgress(_node.id);

        // Background grid:
        //   unlocked + studied   → green  (maintained)
        //   unlocked + !studied  → yellow (warning — decaying)
        //   !unlocked + studied  → teal   (working towards unlock)
        //   !unlocked + !studied → clear  (no engagement; icon tint carries locked-ness)
        if (background != null) {
            if (unlocked && isStudied)
                background.color = new Color(0.45f, 0.70f, 0.45f, 0.6f); // green
            else if (unlocked)
                background.color = new Color(0.85f, 0.75f, 0.30f, 0.55f); // yellow
            else if (isStudied)
                background.color = new Color(0.40f, 0.68f, 0.70f, 0.75f); // teal (greener blue)
            else
                background.color = Color.clear;
        }

        // Cost text: green only while a scientist is actually contributing progress.
        if (cost != null) {
            cost.text  = $"{p:0.0} / {_node.cost:0}";
            cost.color = activelyStudied ? new Color(0.15f, 0.50f, 0.15f) // green — actively being researched
                       : canStudy        ? Color.black
                       :                   new Color(0.55f, 0.55f, 0.55f); // grey — locked
        }

        if (progressBar != null)
            progressBar.SetFill(p / _rs.GetCap(_node));

        if (thresholdMarker != null)
            thresholdMarker.gameObject.SetActive(p > 0f || unlocked);
    }

    public void Setup(ResearchNodeData node, ResearchSystem rs,
                      System.Action<ResearchNodeData> onToggleStudy) {
        _node = node;
        _rs   = rs;

        bool canStudy = rs.CanStudy(node);

        // Icon sprite + tint
        if (icon != null) {
            var sprite = Resources.Load<Sprite>(SpritePath + node.name)
                      ?? Resources.Load<Sprite>(SpriteFallback);
            if (sprite != null) { icon.sprite = sprite; icon.preserveAspect = true; }
            icon.color = (rs.IsUnlocked(node.id) || canStudy) ? Color.white : new Color(0.5f, 0.5f, 0.5f);
        }

        // Study toggle — spans the full card, visible when prereqs are met.
        if (buttonStudy != null) {
            buttonStudy.gameObject.SetActive(canStudy);
            if (canStudy) {
                var captured = node;
                buttonStudy.onClick.AddListener(() => onToggleStudy(captured));
            }
        }

        // Tooltip
        var tip = GetComponent<Tooltippable>();
        if (tip != null) {
            tip.title = node.name;
            tip.body  = ResearchPanel.BuildTooltipBody(node, rs);
        }

        RefreshProgress();
    }
}
