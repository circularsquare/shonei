using TMPro;
using UnityEngine;

// Single source of truth for the project's UI font + base size.
// Lives at Assets/Resources/FontConfig.asset so it can be loaded by name at
// runtime without an inspector reference.
//
// Editing this asset's font/fontSize and clicking "Apply to All" in the
// inspector (or running Tools → Apply Font Config) propagates the change to
// every TMP_Text in every open scene and every prefab in the project. Useful
// for trying new fonts before committing to per-component layout work.
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
}
