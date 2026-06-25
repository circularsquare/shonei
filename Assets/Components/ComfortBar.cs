using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Horizontal range bar showing a plant's comfortable band against a fixed domain.
// The whole track is the "stalled" colour (yellow); a green overlay marks the
// comfortable sub-range; a small circle marker shows the current value.
//
// Domain (e.g. -10..40 C, or 0..100 moisture), caption, unit, and mechanic note
// are authored per instance in the editor, so the same widget serves both the
// temperature and the moisture bar with no per-bar code.
//
// Layout contract (authored in scene):
//   ComfortBar (root)              — this script + Tooltippable (hover = exact values)
//   ├── Label (TMP, optional)      — left caption ("temp" / "moisture")
//   ├── BarBg (Image, yellow)      — stalled-range track, fills the bar area
//   │   ├── GreenZone (Image)      — comfortable band; driven via anchorMin/Max.x
//   │   └── Marker (Image, circle) — current value; driven via anchor.x, fixed px size
//
// Like SkillDisplay's XP bar, the zones resize by anchor (not Image.fillAmount)
// so the rects genuinely grow/shrink rather than just clipping a stretched image.
public class ComfortBar : MonoBehaviour {
    [Header("Domain (authored per instance)")]
    [SerializeField] float  domainLo = -10f;  // value mapped to the bar's left edge
    [SerializeField] float  domainHi = 40f;   // value mapped to the bar's right edge
    [SerializeField] string caption  = "temp";// left label + tooltip noun
    [SerializeField] string unit     = "C";   // suffix in the hover readout ("" for moisture)
    [SerializeField, TextArea] string note = "out of range stops growth"; // appended to hover body

    [Header("Refs")]
    [SerializeField] TextMeshProUGUI label;
    [SerializeField] RectTransform   greenZone;
    [SerializeField] RectTransform   marker;
    [SerializeField] Tooltippable    tooltip;

    void Awake() {
        if (label != null) label.text = caption;
    }

    // Set the comfortable band (null bound = unbounded on that side, so green runs
    // to the domain edge) and the current value (null = unknown, hides the marker).
    // Call every refresh while a plant is shown.
    public void Set(float? comfortLo, float? comfortHi, float? now) {
        float span = domainHi - domainLo;
        if (span <= 0f) {
            Debug.LogError($"ComfortBar '{caption}': domainHi ({domainHi}) must exceed domainLo ({domainLo})");
            return;
        }

        // Green band: clamp the comfortable range into the domain and drive the
        // overlay's horizontal anchors. Full vertical fill, zero offsets.
        if (greenZone != null) {
            float a = Mathf.Clamp01(((comfortLo ?? domainLo) - domainLo) / span);
            float b = Mathf.Clamp01(((comfortHi ?? domainHi) - domainLo) / span);
            greenZone.anchorMin = new Vector2(a, 0f);
            greenZone.anchorMax = new Vector2(b, 1f);
            greenZone.offsetMin = Vector2.zero;
            greenZone.offsetMax = Vector2.zero;
        }

        // Marker: position by horizontal anchor only; keep the prefab's vertical
        // anchoring and fixed pixel size (sizeDelta) so the circle stays crisp.
        if (marker != null) {
            bool show = now.HasValue;
            marker.gameObject.SetActive(show);
            if (show) {
                float t = Mathf.Clamp01((now.Value - domainLo) / span);
                marker.anchorMin = new Vector2(t, marker.anchorMin.y);
                marker.anchorMax = new Vector2(t, marker.anchorMax.y);
                marker.anchoredPosition = new Vector2(0f, marker.anchoredPosition.y);
            }
        }

        // Hover: exact comfortable range + current value, plus the mechanic note.
        // ASCII only (m5x7 font); '?' marks an unbounded side / unknown value.
        if (tooltip != null) {
            string lo  = comfortLo.HasValue ? comfortLo.Value.ToString("0") : "?";
            string hi  = comfortHi.HasValue ? comfortHi.Value.ToString("0") : "?";
            string cur = now.HasValue ? now.Value.ToString("0") + unit : "?";
            tooltip.title = caption;
            tooltip.body  = $"comfortable {lo}-{hi}{unit}\ncurrent {cur}"
                          + (string.IsNullOrEmpty(note) ? "" : "\n" + note);
        }
    }
}
