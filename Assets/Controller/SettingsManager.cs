using System;
using UnityEngine;

// Persistent user preferences (volume, FPS cap, vsync, lighting toggle).
//
// Single source of truth for runtime-tunable settings — distinct from the world
// save file, which holds gameplay state. Settings live in PlayerPrefs so they
// follow the user's machine, not the save slot.
//
// Setup: attach to a GameObject in the scene (next to other controllers). The
// OptionsPanel UI calls Set*() to mutate, then any system that cares can either
// read the property directly or subscribe to OnChanged to react to a change.
//
// Adding a new setting:
//   1. Add a property + backing field with a sensible default.
//   2. Add a Set*() method that writes PlayerPrefs and invokes OnChanged.
//   3. Load it in Load() with the matching default.
//   No edits to consumers needed unless they want to react immediately.
public class SettingsManager : MonoBehaviour { 
    public static SettingsManager instance { get; private set; }

    // ── Keys ─────────────────────────────────────────────────────────────────
    const string K_MasterVol  = "settings.masterVolume";
    const string K_SfxVol     = "settings.sfxVolume";
    const string K_AmbientVol = "settings.ambientVolume";
    const string K_TargetFps  = "settings.targetFps";   // 0 = unlimited
    const string K_Vsync      = "settings.vsync";       // 0 / 1
    const string K_FlatLighting = "settings.flatLighting"; // 0 = shaded, 1 = flat
    const string K_WallShadows = "settings.wallShadows"; // 0 = legacy interior lighting, 1 = wall-occluded point lights
    const string K_CloudLight = "settings.cloudLighting"; // 0 / 1
    const string K_CloudDetail = "settings.cloudDetail"; // 0.2–1 fraction of full blob count
    const string K_ParticleDensity = "settings.particleDensity"; // 0–1 fraction of full precipitation (0 = off)
    const string K_AutosaveMins = "settings.autosaveMinutes"; // 0 = off
    const string K_UiScale    = "settings.uiScale";     // CanvasScaler factor, 1–2
    const string K_UiFontIndex = "settings.uiFontIndex"; // index into UIFontOptions
    const string K_HideBackground = "settings.hideBackground"; // 0 / 1

    // Every settings-owned PlayerPrefs key. ResetToDefaults() clears exactly these — NOT
    // PlayerPrefs.DeleteAll(), which would also nuke the login token (Session) and the
    // save-sync machine GUID (SaveSyncIndex). Add new settings keys here too.
    static readonly string[] AllKeys = {
        K_MasterVol, K_SfxVol, K_AmbientVol, K_TargetFps, K_Vsync, K_FlatLighting, K_WallShadows,
        K_CloudLight, K_CloudDetail, K_ParticleDensity, K_AutosaveMins, K_UiScale, K_UiFontIndex, K_HideBackground,
    };

    // ── Values ───────────────────────────────────────────────────────────────
    public float masterVolume   { get; private set; } = 1f;
    public float sfxVolume      { get; private set; } = 1f;
    public float ambientVolume  { get; private set; } = 1f;
    public int   targetFps      { get; private set; } = 60;
    public bool  vsyncEnabled   { get; private set; } = false;
    // Flat lighting: when on, dynamic sprites (animals/plants/buildings) are captured
    // with flat camera-facing normals → uniform, un-shaded lighting (point lights still
    // light a radial circle). Terrain tiles keep their depth, and deep-interior / sky
    // occlusion is unaffected (that's LightPass, not the normals). A cheaper, better-
    // looking floor than fully-off lighting. Default off = full shaded lighting.
    public bool  flatLighting  { get; private set; } = false;
    // Wall shadows: when on, point lights (torches) are occluded by solid tiles via a
    // per-pixel ray-march, and enclosed-building interiors (burrows) are promoted from the
    // directional-only tier to lit-only so they RECEIVE torchlight (a torch inside lights the
    // burrow; a torch above is blocked by the roof). When off, the legacy behaviour: point
    // lights ignore walls and burrow interiors get sun + ambient only. Default on.
    public bool  wallShadows   { get; private set; } = true;
    // When off, CloudFieldGen Pass 0 skips the 5-tap height-field normal +
    // Lambertian band selection and outputs a flat-colour silhouette. Saves
    // ~80% of the cloud blob-loop work; useful for measuring the cost of the
    // cloud lighting pass on weaker GPUs.
    public bool  cloudLightingEnabled{ get; private set; } = true;
    // Cloud detail: the fraction of the full blob count CloudLayer spawns. The
    // cloud-gen shader loops over every blob for every pixel (no spatial culling),
    // so blob count is a near-linear multiplier on cloud GPU cost. Lowering this
    // spawns fewer, proportionally larger blobs (cell size + radii scale by
    // 1/sqrt(detail)), preserving coverage while shortening the per-pixel loop —
    // a quality/perf knob for weaker GPUs, complementary to cloudLightingEnabled.
    // 1 = full detail (current look); 0.2 = ~5x fewer blobs, chunkier clouds.
    public const float CloudDetailMin = 0.2f;
    public const float CloudDetailMax = 1f;
    public float cloudDetail   { get; private set; } = 1f;
    // Precipitation density: fraction of full rain/snow emission rate (0 = no particles).
    // PrecipitationParticles scales its emission by this each frame; lowering it cuts both
    // GPU overdraw and the per-frame CPU collision sweep (fewer live particles). 0 disables.
    public float particleDensity { get; private set; } = 1f;
    // Autosave interval in minutes; 0 = off. SaveSystem reads this each Update to pace the
    // periodic write to a rotating "autosave" slot.
    public int   autosaveIntervalMinutes { get; private set; } = 5;
    public bool  autosaveEnabled => autosaveIntervalMinutes > 0;
    // Whole-UI zoom, applied as the root CanvasScaler's scaleFactor (Constant Pixel
    // Size mode). 1 = native pixel UI (today), 2 = doubled. Scales every UI widget,
    // font, and icon uniformly. UI text is SDF m5x7 so it stays sharp at intermediate
    // scales. UiMin/UiMax bound the range; values snap to UiScaleStep (0.05 = 2.5% in
    // the 50–100% slider framing, so 75% = 1.50). Keep these in sync with the slider.
    public const float UiScaleMin  = 1f;
    public const float UiScaleMax  = 2f;
    public const float UiScaleStep = 0.05f;
    public float uiScale        { get; private set; } = 1f;
    // Selected UI font (index into UIFontOptions.fonts). 0 = the shipped/baked default.
    // Applied at runtime by UITextRuntimeStyle.
    public int   uiFontIndex    { get; private set; } = 0;
    // Hide the decorative sky background (all SkyLayerBase layers — clouds, hills, gradient,
    // stars, haze), leaving the camera's flat clear color. Applied by BackgroundVisibility.
    public bool  hideBackground { get; private set; } = false;

    // Fired after any setter writes a value. Subscribers re-pull whatever they
    // care about. Cheap because the panel only emits on user input, not per-frame.
    public event Action OnChanged;

    void Awake() {
        if (instance != null && instance != this) {
            Debug.LogError("there should only be one SettingsManager");
            Destroy(gameObject);
            return;
        }
        instance = this;
        Load();
    }

    void Load() {
        masterVolume    = Mathf.Clamp01(PlayerPrefs.GetFloat(K_MasterVol,  1f));
        sfxVolume       = Mathf.Clamp01(PlayerPrefs.GetFloat(K_SfxVol,     1f));
        ambientVolume   = Mathf.Clamp01(PlayerPrefs.GetFloat(K_AmbientVol, 1f));
        targetFps       = Mathf.Max(0, PlayerPrefs.GetInt(K_TargetFps, 60));
        vsyncEnabled    = PlayerPrefs.GetInt(K_Vsync, 0) != 0;
        flatLighting    = PlayerPrefs.GetInt(K_FlatLighting, 0) != 0;
        wallShadows     = PlayerPrefs.GetInt(K_WallShadows, 1) != 0;
        cloudLightingEnabled = PlayerPrefs.GetInt(K_CloudLight, 1) != 0;
        cloudDetail     = Mathf.Clamp(PlayerPrefs.GetFloat(K_CloudDetail, 1f), CloudDetailMin, CloudDetailMax);
        particleDensity = Mathf.Clamp01(PlayerPrefs.GetFloat(K_ParticleDensity, 1f));
        autosaveIntervalMinutes = Mathf.Max(0, PlayerPrefs.GetInt(K_AutosaveMins, 5));
        // First launch: no saved UI scale yet → pick a sensible default from screen height
        // (small screens get native UI, large ones get a zoomed UI) and persist it once, so
        // later launches honour whatever the user set. Existing users keep their saved value.
        if (!PlayerPrefs.HasKey(K_UiScale)) {
            uiScale = DefaultUiScaleForScreen(Screen.height);
            PlayerPrefs.SetFloat(K_UiScale, uiScale);
        } else {
            uiScale = Mathf.Clamp(PlayerPrefs.GetFloat(K_UiScale, 1f), UiScaleMin, UiScaleMax);
        }
        uiFontIndex     = Mathf.Max(0, PlayerPrefs.GetInt(K_UiFontIndex, 0));
        hideBackground  = PlayerPrefs.GetInt(K_HideBackground, 0) != 0;
    }

    // ── Setters ──────────────────────────────────────────────────────────────

    public void SetMasterVolume(float v) {
        v = Mathf.Clamp01(v);
        if (Mathf.Approximately(v, masterVolume)) return;
        masterVolume = v;
        PlayerPrefs.SetFloat(K_MasterVol, v);
        OnChanged?.Invoke();
    }

    public void SetSfxVolume(float v) {
        v = Mathf.Clamp01(v);
        if (Mathf.Approximately(v, sfxVolume)) return;
        sfxVolume = v;
        PlayerPrefs.SetFloat(K_SfxVol, v);
        OnChanged?.Invoke();
    }

    public void SetAmbientVolume(float v) {
        v = Mathf.Clamp01(v);
        if (Mathf.Approximately(v, ambientVolume)) return;
        ambientVolume = v;
        PlayerPrefs.SetFloat(K_AmbientVol, v);
        OnChanged?.Invoke();
    }

    // 0 = unlimited; otherwise the cap in Hz. TimeController clamps the
    // effective targetFrameRate against this.
    public void SetTargetFps(int fps) {
        fps = Mathf.Max(0, fps);
        if (fps == targetFps) return;
        targetFps = fps;
        PlayerPrefs.SetInt(K_TargetFps, fps);
        OnChanged?.Invoke();
    }

    public void SetVsync(bool enabled) {
        if (enabled == vsyncEnabled) return;
        vsyncEnabled = enabled;
        PlayerPrefs.SetInt(K_Vsync, enabled ? 1 : 0);
        OnChanged?.Invoke();
    }

    public void SetFlatLighting(bool flat) {
        if (flat == flatLighting) return;
        flatLighting = flat;
        PlayerPrefs.SetInt(K_FlatLighting, flat ? 1 : 0);
        OnChanged?.Invoke();
    }

    public void SetWallShadows(bool enabled) {
        if (enabled == wallShadows) return;
        wallShadows = enabled;
        PlayerPrefs.SetInt(K_WallShadows, enabled ? 1 : 0);
        OnChanged?.Invoke();
    }

    public void SetCloudLighting(bool enabled) {
        if (enabled == cloudLightingEnabled) return;
        cloudLightingEnabled = enabled;
        PlayerPrefs.SetInt(K_CloudLight, enabled ? 1 : 0);
        OnChanged?.Invoke();
    }

    // Fraction of full cloud blob count (0.2–1). CloudLayer re-reads this each bake.
    public void SetCloudDetail(float v) {
        v = Mathf.Clamp(v, CloudDetailMin, CloudDetailMax);
        if (Mathf.Approximately(v, cloudDetail)) return;
        cloudDetail = v;
        PlayerPrefs.SetFloat(K_CloudDetail, v);
        OnChanged?.Invoke();
    }

    // Fraction of full precipitation emission (0–1; 0 = off). PrecipitationParticles re-reads live.
    public void SetParticleDensity(float v) {
        v = Mathf.Clamp01(v);
        if (Mathf.Approximately(v, particleDensity)) return;
        particleDensity = v;
        PlayerPrefs.SetFloat(K_ParticleDensity, v);
        OnChanged?.Invoke();
    }

    // minutes: 0 = off, otherwise the autosave period. SaveSystem re-reads this live.
    public void SetAutosaveIntervalMinutes(int minutes) {
        minutes = Mathf.Max(0, minutes);
        if (minutes == autosaveIntervalMinutes) return;
        autosaveIntervalMinutes = minutes;
        PlayerPrefs.SetInt(K_AutosaveMins, minutes);
        OnChanged?.Invoke();
    }

    // First-launch UI scale chosen from the display's pixel height. Three tiers mapped to
    // slider travel: short screens get the native UI (bottom), mid screens a half-zoom (middle),
    // tall screens the full zoom (top). Picked once in Load(), then user choice takes over.
    static float DefaultUiScaleForScreen(int screenHeight) {
        if (screenHeight <= 900)  return UiScaleMin;                  // bottom: native UI
        if (screenHeight <= 1400) return (UiScaleMin + UiScaleMax) / 2f; // middle: half zoom
        return UiScaleMax;                                           // top: full zoom
    }

    // Whole-UI zoom factor (CanvasScaler.scaleFactor). UI.cs applies it on change.
    public void SetUiScale(float v) {
        v = Mathf.Round(v / UiScaleStep) * UiScaleStep;   // snap to 2.5% increments
        v = Mathf.Clamp(v, UiScaleMin, UiScaleMax);
        if (Mathf.Approximately(v, uiScale)) return;
        uiScale = v;
        PlayerPrefs.SetFloat(K_UiScale, v);
        OnChanged?.Invoke();
    }

    // Index into UIFontOptions.fonts. UITextRuntimeStyle re-reads this on OnChanged and swaps the
    // whole UI's font.
    public void SetUiFontIndex(int i) {
        i = Mathf.Max(0, i);
        if (i == uiFontIndex) return;
        uiFontIndex = i;
        PlayerPrefs.SetInt(K_UiFontIndex, i);
        OnChanged?.Invoke();
    }

    // Hide/show the decorative sky background. BackgroundVisibility re-reads on OnChanged.
    public void SetHideBackground(bool enabled) {
        if (enabled == hideBackground) return;
        hideBackground = enabled;
        PlayerPrefs.SetInt(K_HideBackground, enabled ? 1 : 0);
        OnChanged?.Invoke();
    }

    // Wipe every settings key and re-load, so each value falls back to its built-in default
    // (uiScale re-derives from the current screen, as on a fresh install). Fires OnChanged
    // once so all subscribers — CanvasScaler, audio, lighting, the open OptionsPanel — re-pull.
    // Leaves non-settings prefs (login token, machine GUID) untouched. See AllKeys.
    public void ResetToDefaults() {
        foreach (var k in AllKeys) PlayerPrefs.DeleteKey(k);
        Load();
        OnChanged?.Invoke();
    }

    // Force a flush — Unity normally flushes on quit, but games may crash.
    void OnApplicationQuit() {
        PlayerPrefs.Save();
    }
}
