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
    const string K_Lighting   = "settings.lighting";    // 0 / 1

    // ── Values ───────────────────────────────────────────────────────────────
    public float masterVolume   { get; private set; } = 1f;
    public float sfxVolume      { get; private set; } = 1f;
    public float ambientVolume  { get; private set; } = 1f;
    public int   targetFps      { get; private set; } = 60;
    public bool  vsyncEnabled   { get; private set; } = false;
    public bool  lightingEnabled{ get; private set; } = true;

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
        lightingEnabled = PlayerPrefs.GetInt(K_Lighting, 1) != 0;
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

    public void SetLighting(bool enabled) {
        if (enabled == lightingEnabled) return;
        lightingEnabled = enabled;
        PlayerPrefs.SetInt(K_Lighting, enabled ? 1 : 0);
        OnChanged?.Invoke();
    }

    // Force a flush — Unity normally flushes on quit, but games may crash.
    void OnApplicationQuit() {
        PlayerPrefs.Save();
    }
}
