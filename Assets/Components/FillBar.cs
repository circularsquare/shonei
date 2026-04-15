using UnityEngine;
using UnityEngine.UI;

// Reusable horizontal fill bar driven by a 0–1 fraction.
//
// Prefab setup:
//   Root GO:
//     Image component — background (e.g. #333333)
//     LayoutElement   — set preferred width and height here
//     FillBar script  — wire fillImage below
//   Child "Fill" GO:
//     RectTransform — anchor min (0,0) max (1,1), offsets all zero
//     Image component — fill color (e.g. white or #cccccc)
//       Image Type   = Filled
//       Fill Method  = Horizontal
//       Fill Origin  = Left
//     ← assign this Image to fillImage in FillBar
public class FillBar : MonoBehaviour {
    [SerializeField] Image fillImage;

    public void SetFill(float fraction) {
        if (fillImage != null)
            fillImage.fillAmount = Mathf.Clamp01(fraction);
    }
}
