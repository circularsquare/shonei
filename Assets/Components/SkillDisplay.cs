using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Compact skill widget: icon (with name tooltip) + "lv1" label + XP progress bar (with xp tooltip).
/// Call Setup() once when instantiated, Refresh() whenever skill data changes.
/// Sprite loaded from Sprites/Skills/{skillname}; falls back to Sprites/Skills/default.
/// </summary>
public class SkillDisplay : MonoBehaviour {
    const string SpritePath     = "Sprites/Skills/";
    const string SpriteFallback = "Sprites/Skills/default";

    [Header("Prefab Refs")]
    [SerializeField] Image           icon;
    [SerializeField] TextMeshProUGUI levelLabel;
    [SerializeField] Image           progressBar;
    [SerializeField] Tooltippable    iconTooltip;
    [SerializeField] Tooltippable    barTooltip;

    Skill    _skill;
    SkillSet _skills;

    public void Setup(Skill skill, SkillSet skillSet) {
        _skill  = skill;
        _skills = skillSet;

        // Icon sprite — lowercase skill name, fallback to default
        string spriteName = skill.ToString().ToLower();
        var sprite = Resources.Load<Sprite>(SpritePath + spriteName)
                  ?? Resources.Load<Sprite>(SpriteFallback);
        if (icon != null) { icon.sprite = sprite; icon.preserveAspect = true; }

        // Icon tooltip shows just the skill name
        if (iconTooltip != null) { iconTooltip.title = skill.ToString(); iconTooltip.body = ""; }

        // Bar fill color (type stays Simple — fill is driven by anchorMax.x in Refresh)
        if (progressBar != null) progressBar.color = new Color(0.25f, 0.7f, 0.25f, 0.9f);

        Refresh();
    }

    public void Refresh() {
        if (_skills == null) return;
        int   lv        = _skills.GetLevel(_skill);
        float xp        = _skills.GetXp(_skill);
        float threshold = SkillSet.XpThreshold(lv);
        float fill      = Mathf.Clamp01(xp / threshold);

        if (levelLabel != null) levelLabel.text = $"lv{lv}";

        // Anchor-based fill: anchorMax.x drives the right edge of BarFill
        // proportionally within BarBg, so the rect actually shrinks/grows.
        // Image.Type.Filled only clips rendering but keeps the rect the same
        // size, which looks identical to a full bar when the image is stretched.
        if (progressBar != null) {
            var rt = progressBar.rectTransform;
            rt.anchorMin = new Vector2(0f,    rt.anchorMin.y);
            rt.anchorMax = new Vector2(fill,  rt.anchorMax.y);
            rt.offsetMin = new Vector2(0f,    rt.offsetMin.y);
            rt.offsetMax = new Vector2(0f,    rt.offsetMax.y);
        }

        if (barTooltip != null) barTooltip.body = $"{xp:F1}/{threshold:F0}";
    }
}
