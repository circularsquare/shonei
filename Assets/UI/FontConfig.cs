using TMPro;
using UnityEngine;

// Single source of truth for the project's UI font + base size + primary text color.
// Lives at Assets/Resources/FontConfig.asset so it can be loaded by name at
// runtime without an inspector reference.
//
// Editing this asset's font/fontSize/primaryTextColor and clicking "Apply to All"
// in the inspector (or running Tools → Apply Font Config) propagates the change to
// every TMP_Text in every open scene and every prefab in the project. Font + size
// apply universally; color only normalizes near-black/dark-gray body text (see
// IsPrimaryTextColor), so colored/light/faded labels keep their deliberate colors.
//
// Runtime-created TMP_Text components (instantiated from prefabs) inherit
// from their prefab — which Apply has already updated. For TMP_Text created
// purely in code, call FontConfig.Apply(text) after construction.
[CreateAssetMenu(fileName = "FontConfig", menuName = "Shonei/UI Font Config")]
public class FontConfig : ScriptableObject {
    [Tooltip("UI font asset used by all TMP_Text components in the project. " +
             "After changing, click 'Apply to All' in the inspector.")]
    public TMP_FontAsset font;

    [Tooltip("Base font size applied to all TMP_Text components.")]
    public float fontSize = 16f;

    [Tooltip("Primary (body/heading) text color. 'Apply to All' normalizes every " +
             "near-black/dark-gray opaque label to this — leaving colored, light/medium " +
             "gray, and faded (alpha<1) text untouched.")]
    public Color primaryTextColor = Color.black;

    [Tooltip("Snap UI text baselines to whole device pixels (UITextRuntimeStyle). Keeps a " +
             "pixel font crisp/uniform; matters less for a smooth vector font. Toggling " +
             "this takes effect live in Play mode — no Apply needed.")]
    public bool pixelSnap = true;

    static FontConfig _cached;

    // Lazy-loaded singleton. Path is "FontConfig" (without folder/extension)
    // because Resources.Load resolves that to Assets/Resources/FontConfig.asset.
    public static FontConfig instance {
        get {
            if (_cached == null) _cached = Resources.Load<FontConfig>("FontConfig");
            if (_cached == null) Debug.LogError("FontConfig: Assets/Resources/FontConfig.asset not found");
            return _cached;
        }
    }

    // Call after creating a TMP_Text purely in code (not via prefab instantiate).
    // Returns true if a property changed, false if it was already in sync.
    public static bool Apply(TMP_Text t) {
        var cfg = instance;
        if (cfg == null || cfg.font == null || t == null) return false;
        bool changed = false;
        if (t.font != cfg.font)                              { t.font = cfg.font; changed = true; }
        if (!Mathf.Approximately(t.fontSize, cfg.fontSize))  { t.fontSize = cfg.fontSize; changed = true; }
        return changed;
    }

    // Normalize a "primary" text color to primaryTextColor. Returns true if changed.
    // Only touches near-black/dark-gray opaque neutral text (body/headings) — leaves
    // chromatic state colors, light/medium grays, and faded (alpha<1) text alone, so a
    // blanket apply can't clobber deliberately-distinct colors.
    public static bool ApplyPrimaryColor(TMP_Text t) {
        var cfg = instance;
        if (cfg == null || t == null) return false;
        if (!IsPrimaryTextColor(t.color)) return false;
        if (t.color == cfg.primaryTextColor) return false;
        t.color = cfg.primaryTextColor;
        return true;
    }

    // A color counts as "primary text" if it's opaque, neutral (r≈g≈b), and dark.
    // Catches pure black and the legacy 0.196 "secondary" dark gray; excludes the
    // 0.33-at-half-alpha medium gray, chromatic state tints, and any light gray.
    public static bool IsPrimaryTextColor(Color c) {
        if (c.a < 0.95f) return false;                              // faded → deliberate
        float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        float min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
        if (max - min > 0.04f) return false;                        // chromatic → leave
        return max < 0.30f;                                         // dark only (0.196 yes, 0.33 no)
    }
}
