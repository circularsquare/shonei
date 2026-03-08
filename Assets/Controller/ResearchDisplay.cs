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

    const string SpritePath     = "Sprites/Researches/";
    const string SpriteFallback = "Sprites/Researches/default";

    public void Setup(ResearchNodeData node, ResearchSystem rs, System.Action<ResearchNodeData> onUnlock) {
        bool unlocked  = rs.IsUnlocked(node.id);
        bool canUnlock = rs.CanUnlock(node);

        // Background tint
        if (background != null)
            background.color = unlocked  ? new Color(0.20f, 0.45f, 0.20f, 0.6f)
                             : canUnlock ? new Color(0.20f, 0.35f, 0.55f, 0.6f)
                             :             new Color(0.20f, 0.20f, 0.20f, 0.6f);

        // Icon sprite + tint
        if (icon != null) {
            var sprite = Resources.Load<Sprite>(SpritePath + node.name)
                      ?? Resources.Load<Sprite>(SpriteFallback);
            if (sprite != null) { icon.sprite = sprite; icon.preserveAspect = true; }
            icon.color = (unlocked || canUnlock) ? Color.white : new Color(0.5f, 0.5f, 0.5f);
        }

        // Cost label
        if (cost != null) {
            cost.text  = unlocked ? "done" : $"{node.cost:0}";
            cost.color = unlocked  ? new Color(0.4f, 0.9f, 0.4f)
                       : canUnlock ? Color.white
                       :             new Color(0.55f, 0.55f, 0.55f);
        }

        // Unlock button
        if (buttonResearch != null) {
            buttonResearch.gameObject.SetActive(canUnlock);
            if (canUnlock) {
                var captured = node;
                buttonResearch.onClick.AddListener(() => onUnlock(captured));
                var cb = buttonResearch.colors;
                cb.highlightedColor = new Color(1f, 1f, 1f, 0.85f);
                buttonResearch.colors = cb;
            }
        }

        // Tooltip
        var tip = GetComponent<Tooltippable>();
        if (tip != null) {
            tip.title = node.name;
            tip.body  = ResearchPanel.BuildTooltipBody(node, rs);
        }
    }
}
