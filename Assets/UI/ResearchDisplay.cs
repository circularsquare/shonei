using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Attach to the ResearchDisplay prefab.
// Wires up the card's child components from the prefab hierarchy.
public class ResearchDisplay : MonoBehaviour {
    [Header("Prefab Refs")]
    public Image            background;
    public Image            icon;
    public TextMeshProUGUI  cost;
    public Button           buttonResearch;
    public Image            progressBarGreen;
    public Image            progressBarBlue;

    const string SpritePath     = "Sprites/Researches/";
    const string SpriteFallback = "Sprites/Researches/default";

    ResearchNodeData _node;
    ResearchSystem   _rs;

    // Lightweight refresh — updates progress text and background tint without rebuilding the card.
    public void RefreshProgress() {
        if (_node == null || _rs == null) return;
        bool unlocked    = _rs.IsUnlocked(_node.id);
        bool canSetActive = _rs.CanSetActive(_node);
        bool isActive    = _rs.activeResearchId == _node.id;
        float p          = _rs.GetProgress(_node.id);

        if (background != null)
            background.color = isActive    ? new Color(0.55f, 0.45f, 0.10f, 0.8f)
                             : unlocked    ? new Color(0.20f, 0.45f, 0.20f, 0.6f)
                             : canSetActive ? new Color(0.20f, 0.35f, 0.55f, 0.6f)
                             :               new Color(0.20f, 0.20f, 0.20f, 0.6f);

        if (cost != null) {
            cost.text  = $"{p:0.0} / {_node.cost:0}";
            cost.color = unlocked    ? new Color(0.4f, 0.9f, 0.4f)
                       : canSetActive ? Color.white
                       :               new Color(0.55f, 0.55f, 0.55f);
        }

        float cap = _node.cost;
        if (progressBarGreen != null) progressBarGreen.fillAmount = Mathf.Clamp01(p / cap);
        if (progressBarBlue  != null) progressBarBlue.fillAmount  = Mathf.Clamp01((p - cap) / cap);
    }

    public void Setup(ResearchNodeData node, ResearchSystem rs, System.Action<ResearchNodeData> onSetActive) {
        _node = node;
        _rs   = rs;

        // Icon sprite + tint (static — doesn't change after setup)
        bool canSetActive = rs.CanSetActive(node);
        if (icon != null) {
            var sprite = Resources.Load<Sprite>(SpritePath + node.name)
                      ?? Resources.Load<Sprite>(SpriteFallback);
            if (sprite != null) { icon.sprite = sprite; icon.preserveAspect = true; }
            icon.color = (rs.IsUnlocked(node.id) || canSetActive) ? Color.white : new Color(0.5f, 0.5f, 0.5f);
        }

        // Set-active button — shown when prereqs are met (allows both setting and toggling off)
        if (buttonResearch != null) {
            buttonResearch.gameObject.SetActive(canSetActive);
            if (canSetActive) {
                var captured = node;
                buttonResearch.onClick.AddListener(() => onSetActive(captured));
                var cb = buttonResearch.colors;
                cb.highlightedColor = new Color(1f, 1f, 1f, 0.85f);
                buttonResearch.colors = cb;
            }
        }

        // Progress bars — configure fill mode and color
        void InitBar(Image bar, Color color) {
            if (bar == null) return;
            bar.type       = Image.Type.Filled;
            bar.fillMethod = Image.FillMethod.Horizontal;
            bar.fillOrigin = (int)Image.OriginHorizontal.Left;
            bar.color      = color;
        }
        InitBar(progressBarGreen, new Color(0.25f, 0.8f, 0.25f, 0.9f));
        InitBar(progressBarBlue,  new Color(0.25f, 0.5f, 0.9f,  0.9f));

        // Tooltip
        var tip = GetComponent<Tooltippable>();
        if (tip != null) {
            tip.title = node.name;
            tip.body  = ResearchPanel.BuildTooltipBody(node, rs);
        }

        RefreshProgress();
    }
}
