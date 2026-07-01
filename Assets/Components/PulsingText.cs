using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Pulses a TMP label's opacity in a sine wave. Used for the "new" recipe badge in the recipes
// panel. Drives alpha only (keeps the authored RGB), on UNSCALED time so every badge pulses in
// unison and the pulse keeps going while the game is paused.
//
// The badge itself is built in code (see Create) because it's a tiny per-row addition to
// runtime-instantiated list rows — the sanctioned exception to scene-authored UI. Font + size are
// left for UITextRuntimeStyle to stamp (it never touches color), so the badge tracks the player's
// UI font while keeping its red-orange tint.
public class PulsingText : MonoBehaviour {
    [SerializeField] TMP_Text text;
    public float min = 0.35f, max = 0.9f, speed = 3f;

    void Awake() { if (text == null) text = GetComponent<TMP_Text>(); }

    void Update() {
        if (text == null) return;
        float t = (Mathf.Sin(Time.unscaledTime * speed) + 1f) * 0.5f; // 0..1
        Color c = text.color;
        c.a = Mathf.Lerp(min, max, t);
        text.color = c;
    }

    // Red-orange, used for both the recipe-row and workstation-header "new" badges.
    static readonly Color NewColor = new Color(0.90f, 0.38f, 0.15f, 0.85f);
    const float BadgeWidth  = 28f; // fixed cell width — big enough for "new" in either UI font
    const float BadgeHeight = 16f;

    // Builds a pulsing "new" badge as the last child of `parent` (an HLG cell). Returns the label
    // so the caller can toggle its GameObject active/inactive per new-state. Fixed width keeps the
    // pixel font crisp (no fractional-width layout blur) — see the layout-blur SPEC notes.
    public static TMP_Text CreateNewBadge(Transform parent) {
        var go = new GameObject("New", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.pivot = new Vector2(0f, 1f);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = "new";
        tmp.color = NewColor;
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.alignment = TextAlignmentOptions.BottomLeft; // Bottom/Left keeps m5x7 crisp (center blurs)
        var fc = FontConfig.instance;                    // seed font so first frame isn't the TMP default
        if (fc != null && fc.font != null) { tmp.font = fc.font; tmp.fontSize = fc.fontSize; }

        var le = go.AddComponent<LayoutElement>();
        le.minWidth = le.preferredWidth = BadgeWidth;
        le.minHeight = le.preferredHeight = BadgeHeight;
        le.flexibleWidth = 0;

        go.AddComponent<PulsingText>().text = tmp;
        return tmp;
    }
}
