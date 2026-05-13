using System.Collections.Generic;
using UnityEngine;

// Lightweight sound manager — plays one-shot SFX and ambient loops.
//
// Setup: attach to a GameObject in the scene. Self-creates two AudioSources
// in Awake (sfx + ambient). Clips are loaded from Resources/Audio/ on first use.
//
// SFX:     SoundManager.instance.PlaySFX("blueprint_place")
//          → loads Resources/Audio/SFX/blueprint_place
//
// Ambient: rain loop volume is driven each frame by WeatherSystem.rainAmount.
//          To add more ambient loops, extend the ambient handling in Update().
public class SoundManager : MonoBehaviour {
    public static SoundManager instance { get; private set; }

    // Designer baselines — relative loudness between buses, tuned in editor.
    // The runtime multiplies these by the user's SettingsManager sliders
    // (master × per-bus) at point-of-use. So "max user volume" plays a clip at
    // exactly this baseline; raining at max plays at exactly this ambient baseline.
    [Header("Volume baselines")]
    [SerializeField, Range(0f, 1f)] float sfxVolume     = 1f;
    [SerializeField, Range(0f, 1f)] float ambientVolume  = 0.4f;

    AudioSource sfxSource;
    AudioSource ambientSource;

    // Cache loaded clips so we only hit Resources.Load once per clip name.
    Dictionary<string, AudioClip> clipCache = new();

    // Currently assigned ambient clip name (to avoid re-assigning every frame).
    string currentAmbientClip;

    // True while the rain loop is paused because the game is paused. Tracked so we
    // only call AudioSource.Pause/UnPause on the pause↔resume transitions, not every
    // frame, and so we can resume mid-clip instead of re-attacking the sample.
    bool ambientPausedByGame;

    void Awake() {
        if (instance != null && instance != this) {
            Destroy(gameObject);
            return;
        }
        instance = this;

        sfxSource = gameObject.AddComponent<AudioSource>();
        sfxSource.playOnAwake = false;

        ambientSource = gameObject.AddComponent<AudioSource>();
        ambientSource.playOnAwake = false;
        ambientSource.loop = true;
    }

    void Update() {
        UpdateRainAmbient();
    }

    // --- SFX ---

    /// Play a one-shot clip from Resources/Audio/SFX/{clipName}.
    public void PlaySFX(string clipName) {
        AudioClip clip = GetClip("Audio/SFX/" + clipName);
        if (clip == null) {
            Debug.LogError($"[SoundManager] SFX clip not found: Audio/SFX/{clipName}");
            return;
        }
        sfxSource.PlayOneShot(clip, sfxVolume * UserMaster() * UserSfx());
    }

    // ── User-volume helpers ──
    // Read SettingsManager live; tolerate missing instance (returns 1 = no scaling).
    static float UserMaster() => SettingsManager.instance != null ? SettingsManager.instance.masterVolume  : 1f;
    static float UserSfx()    => SettingsManager.instance != null ? SettingsManager.instance.sfxVolume     : 1f;
    static float UserAmbient()=> SettingsManager.instance != null ? SettingsManager.instance.ambientVolume : 1f;

    // --- Ambient (rain) ---

    void UpdateRainAmbient() {
        // While the game is paused, silence the rain loop but keep the clip's
        // playhead via AudioSource.Pause() — resuming continues mid-loop instead
        // of re-triggering the sample's attack on every space-tap.
        if (Time.timeScale == 0f) {
            if (!ambientPausedByGame && ambientSource.isPlaying) {
                ambientSource.Pause();
                ambientPausedByGame = true;
            }
            return;
        }
        if (ambientPausedByGame) {
            ambientSource.UnPause();
            ambientPausedByGame = false;
        }

        float rain = WeatherSystem.instance?.rainAmount ?? 0f;

        // Step-then-ramp: as soon as there's any rain at all, jump straight
        // to 50% of the ambient baseline, then ramp linearly to 100% at full
        // rain. Gives a snappy "it just started raining" cue instead of
        // creeping in quietly alongside the particle fade-in.
        float curve = rain > 0f ? 0.5f + 0.5f * rain : 0f;
        float vol = curve * ambientVolume * UserMaster() * UserAmbient();

        if (vol > 0.005f) {
            SetAmbientClip("rain");
            ambientSource.volume = vol;
            if (!ambientSource.isPlaying) ambientSource.Play();
        } else {
            if (ambientSource.isPlaying) ambientSource.Stop();
        }
    }

    void SetAmbientClip(string clipName) {
        if (currentAmbientClip == clipName) return;
        AudioClip clip = GetClip("Audio/Ambient/" + clipName);
        if (clip == null) {
            Debug.LogError($"[SoundManager] Ambient clip not found: Audio/Ambient/{clipName}");
            return;
        }
        ambientSource.clip = clip;
        currentAmbientClip = clipName;
    }

    // --- Clip cache ---

    AudioClip GetClip(string resourcePath) {
        if (clipCache.TryGetValue(resourcePath, out AudioClip cached)) return cached;
        AudioClip clip = Resources.Load<AudioClip>(resourcePath);
        if (clip != null) clipCache[resourcePath] = clip;
        return clip;
    }
}
