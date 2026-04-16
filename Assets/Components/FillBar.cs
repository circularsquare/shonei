using UnityEngine;
using UnityEngine.UI;

// Reusable horizontal fill bar driven by a 0–1 fraction.
//
// Supports two fill modes:
//   1. Mask mode (for Sliced images): assign fillMask. Fill is controlled by
//      adjusting the RectMask2D's right padding to clip the bar image.
//   2. Fill mode (legacy): assign fillImage. Uses Image.fillAmount directly.
//      Doesn't work with Sliced image type.
// If both are assigned, mask mode takes priority.
//
// Prefab setup (mask mode):
//   Root GO:
//     Image component — background
//     LayoutElement   — set preferred width and height
//     FillBar script  — wire fillMask below
//   Child "FillMask" GO:
//     RectMask2D      — add component, assign to fillMask
//     RectTransform   — anchor min (0,0) max (1,1), offsets all zero
//   Grandchild "Fill" GO:
//     RectTransform   — anchor min (0,0) max (1,1), offsets all zero
//     Image component — Sliced, bar sprite (e.g. Misc/progressbarresearch)
public class FillBar : MonoBehaviour {
    [SerializeField] RectMask2D fillMask;
    [SerializeField] Image fillImage;

    public void SetFill(float fraction) {
        fraction = Mathf.Clamp01(fraction);
        if (fillMask != null) {
            float width = ((RectTransform)fillMask.transform).rect.width;
            if (width <= 0f) return; // layout not computed yet — skip, next refresh will catch it
            float rightPad = width * (1f - fraction);
            fillMask.padding = new Vector4(0f, 0f, rightPad, 0f);
        } else if (fillImage != null) {
            fillImage.fillAmount = fraction;
        }
    }
}
