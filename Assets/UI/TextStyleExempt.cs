using UnityEngine;

// Marker: opt a TextMeshProUGUI out of UITextRuntimeStyle's font/size stamping.
//
// UITextRuntimeStyle normally forces the player's chosen font + the uniform UI
// size onto every overlay label. That's right for body text, but wrong for the
// rare intentional outlier — a splash/menu title (32/48pt), placeholder title
// text standing in for art, etc. Add this component and the styler keeps the
// label's authored font and size. Pixel-snap still applies (stays crisp).
public class TextStyleExempt : MonoBehaviour {
}
