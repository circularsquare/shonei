using System.Collections.Generic;
using UnityEngine;

// Lightweight sound manager — plays one-shot SFX and ambient loops.
//
// Setup: attach to a GameObject in the scene. Self-creates three AudioSources
// in Awake (sfx + rain + wind). Clips are loaded from Resources/Audio/ on first use.
//
// SFX:     SoundManager.instance.PlaySFX("click")
//          → loads Resources/Audio/SFX/click
//
// Ambient: two independent loops driven each frame by WeatherSystem — rain volume
//          by rainAmount, wind volume by |wind|. They mix freely (it can be windy
//          AND raining). Add more loops by following the same per-source pattern
//          in Update().
public class SoundManager : MonoBehaviour {
    public static SoundManager instance { get; private set; }

    // Designer baselines — relative loudness between buses, tuned in editor.
    // The runtime multiplies these by the user's SettingsManager sliders
    // (master × per-bus) at point-of-use. So "max user volume" plays a clip at
    // exactly this baseline; raining at max plays at exactly this ambient baseline.
    [Header("Volume baselines")]
    [SerializeField, Range(0f, 1f)] float sfxVolume     = 1f;
    [SerializeField, Range(0f, 1f)] float ambientVolume = 0.4f;  // rain loop baseline
    [SerializeField, Range(0f, 1f)] float windVolume    = 0.25f; // wind loop baseline — kept lower; a subtle bed

    AudioSource sfxSource;
    AudioSource ambientSource; // rain loop
    AudioSource windSource;    // wind loop (mixes alongside rain)

    // Cache loaded clips so we only hit Resources.Load once per clip name.
    Dictionary<string, AudioClip> clipCache = new();

    // Clip names we've already logged as missing — so a not-yet-authored SFX
    // logs ONE error, not one per trigger (a missing place/click sound would
    // otherwise spam the console on every action). Cleared never; per-session.
    HashSet<string> warnedMissing = new();

    // Currently assigned ambient clip name (to avoid re-assigning every frame).
    string currentAmbientClip;

    // True while the ambient loops (rain + wind) are paused because the game is
    // paused. Tracked so we only call AudioSource.Pause/UnPause on the pause↔resume
    // transitions, not every frame, and so loops resume mid-clip instead of
    // re-attacking the sample.
    bool ambientPausedByGame;

    // Time-smoothed copies of each loop's shaping curve. The actual volume eases
    // toward its weather-driven target at a fixed rate so changes fade in/out
    // instead of popping.
    float smoothedRainCurve;
    float smoothedWindCurve;
    const float ambientRampSeconds = 3f;

    // Wind loop only plays when |wind| exceeds windThreshold (calm air = silent);
    // from there it scales up to full windVolume at windFullMagnitude. Typical drift
    // is ~±0.4 (see WeatherSystem); gusts go higher.
    const float windThreshold     = 0.3f;
    const float windFullMagnitude = 0.9f;

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

        windSource = gameObject.AddComponent<AudioSource>();
        windSource.playOnAwake = false;
        windSource.loop = true;
    }

    // Bindings to model-side gameplay events that should make a sound but have no
    // UI moment to hang off of. Subscribe in Start (not Awake) per the project's
    // subscription-order guidance; these are static events so they survive the
    // ResearchSystem being recreated on load. Mirror EventFeed's binding style —
    // add new gameplay→SFX hooks here as the list grows.
    void Start() {
        if (instance != this) return; // a duplicate destroyed in Awake shouldn't bind
        ResearchSystem.OnTechUnlocked += HandleTechUnlocked;
    }

    void OnDestroy() {
        ResearchSystem.OnTechUnlocked -= HandleTechUnlocked;
    }

    void HandleTechUnlocked(ResearchNodeData node) => PlaySFX("research_complete");

    void Update() {
        // Both ambient loops pause/resume together with the game, keeping their
        // playheads (so a space-tap doesn't re-attack the loop on every resume).
        if (Time.timeScale == 0f) {
            if (!ambientPausedByGame) {
                if (ambientSource.isPlaying) ambientSource.Pause();
                if (windSource.isPlaying)    windSource.Pause();
                ambientPausedByGame = true;
            }
            return;
        }
        if (ambientPausedByGame) {
            ambientSource.UnPause();
            windSource.UnPause();
            ambientPausedByGame = false;
        }
        UpdateRainAmbient();
        UpdateWindAmbient();
    }

    // --- SFX ---

    // Play a one-shot clip from Resources/Audio/SFX/{clipName}.
    // volumeScale (0..1) trims an individual clip relative to the SFX baseline —
    // for clips that are inherently hotter than the rest and need ducking.
    public void PlaySFX(string clipName, float volumeScale = 1f) {
        AudioClip clip = GetClip("Audio/SFX/" + clipName);
        if (clip == null) {
            // Warn once per missing clip name, then stay quiet — a SFX slot that
            // doesn't have its file yet shouldn't spam an error on every trigger.
            if (warnedMissing.Add(clipName))
                Debug.LogError($"[SoundManager] SFX clip not found: Audio/SFX/{clipName}");
            return;
        }
        sfxSource.PlayOneShot(clip, sfxVolume * volumeScale * UserMaster() * UserSfx());
    }

    // ── User-volume helpers ──
    // Read SettingsManager live; tolerate missing instance (returns 1 = no scaling).
    static float UserMaster() => SettingsManager.instance != null ? SettingsManager.instance.masterVolume  : 1f;
    static float UserSfx()    => SettingsManager.instance != null ? SettingsManager.instance.sfxVolume     : 1f;
    static float UserAmbient()=> SettingsManager.instance != null ? SettingsManager.instance.ambientVolume : 1f;

    // --- Ambient (rain) ---

    void UpdateRainAmbient() {
        float rain = WeatherSystem.instance?.rainAmount ?? 0f;

        // Target curve: as soon as there's any rain at all, aim for 50% of the
        // rain baseline, then climb linearly to 100% at full rain. Then time-smooth
        // toward that target so onsets fade in (and cessations fade out) over
        // ambientRampSeconds rather than popping. Tied to scaled deltaTime — pause
        // is handled in Update(), and fast-forward pulls the ramp through proportionally.
        float targetCurve = rain > 0f ? 0.5f + 0.5f * rain : 0f;
        smoothedRainCurve = Mathf.MoveTowards(smoothedRainCurve, targetCurve, Time.deltaTime / ambientRampSeconds);
        float vol = smoothedRainCurve * ambientVolume * UserMaster() * UserAmbient();

        if (vol > 0.005f) {
            SetAmbientClip("rain");
            ambientSource.volume = vol;
            if (!ambientSource.isPlaying) ambientSource.Play();
        } else {
            if (ambientSource.isPlaying) ambientSource.Stop();
        }
    }

    // --- Ambient (wind) ---

    void UpdateWindAmbient() {
        // Only audible on breezier stretches: silent below windThreshold, swelling to
        // full at windFullMagnitude. Driven by |wind| — direction doesn't matter, only
        // strength. (wind drifts via an OU random walk, ~±0.4 typical; gusts go higher.)
        float mag = WeatherSystem.instance != null ? Mathf.Abs(WeatherSystem.instance.wind) : 0f;
        // Silent until the breeze crosses windThreshold, then scale from there to full.
        float strength = Mathf.Clamp01((mag - windThreshold) / (windFullMagnitude - windThreshold));
        // Gentle floor at onset, climbing to full on gusts; below threshold → silent.
        float targetCurve = mag > windThreshold ? 0.35f + 0.65f * strength : 0f;
        smoothedWindCurve = Mathf.MoveTowards(smoothedWindCurve, targetCurve, Time.deltaTime / ambientRampSeconds);
        float vol = smoothedWindCurve * windVolume * UserMaster() * UserAmbient();

        if (vol > 0.005f) {
            if (windSource.clip == null) {
                windSource.clip = GetClip("Audio/Ambient/wind");
                if (windSource.clip == null) {
                    if (warnedMissing.Add("Ambient/wind"))
                        Debug.LogError("[SoundManager] Ambient clip not found: Audio/Ambient/wind");
                    return;
                }
            }
            windSource.volume = vol;
            if (!windSource.isPlaying) windSource.Play();
        } else {
            if (windSource.isPlaying) windSource.Stop();
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
