using System;
using TMPro;
using UnityEngine;

// Player-selectable UI fonts for the in-game font switcher (Options panel).
// Each entry pairs a TMP SDF font asset with the fontSize that makes it visually match the
// others (a compact pixel font and a smooth vector font need different point sizes for the
// same apparent size). Lives at Assets/Resources/UIFontOptions.asset so it loads by name at
// runtime. The selected entry is SettingsManager.uiFontIndex; UITextPixelSnap stamps that
// entry's font+size onto every overlay UI label at runtime (existing and dynamically spawned).
//
// Entry 0 should be the project's shipped/baked default (so an unset preference matches what
// FontConfig baked). Add a font: create its TMP SDF asset, then add an entry here.
[CreateAssetMenu(fileName = "UIFontOptions", menuName = "Shonei/UI Font Options")]
public class UIFontOptions : ScriptableObject {
    // [Serializable] is required here for Unity to serialize the nested list (this is Unity
    // serialization, not the Newtonsoft save path — the no-[Serializable] rule is about save data).
    [Serializable]
    public class Entry {
        [Tooltip("Player-facing name shown in the dropdown.")]
        public string name = "Font";
        public TMP_FontAsset font;
        [Tooltip("Point size tuned so this font matches the others' apparent size.")]
        public float size = 16f;
    }

    public Entry[] fonts;

    static UIFontOptions _cached;

    // Reset the static cache on every Play entry — Reload Domain is off in this project, so a
    // plain static ref would survive scene reloads and point at a stale (possibly destroyed) asset.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCache() { _cached = null; }

    public static UIFontOptions instance {
        get {
            if (_cached == null) _cached = Resources.Load<UIFontOptions>("UIFontOptions");
            if (_cached == null) Debug.LogError("UIFontOptions: Assets/Resources/UIFontOptions.asset not found");
            return _cached;
        }
    }

    // Clamped lookup; null if the registry is empty/missing.
    public Entry Get(int index) {
        if (fonts == null || fonts.Length == 0) return null;
        return fonts[Mathf.Clamp(index, 0, fonts.Length - 1)];
    }
}
