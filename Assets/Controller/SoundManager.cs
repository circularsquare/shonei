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

    [Header("Volume")]
    [SerializeField, Range(0f, 1f)] float sfxVolume     = 1f;
    [SerializeField, Range(0f, 1f)] float ambientVolume  = 0.4f;

    AudioSource sfxSource;
    AudioSource ambientSource;

    // Cache loaded clips so we only hit Resources.Load once per clip name.
    Dictionary<string, AudioClip> clipCache = new();

    // Currently assigned ambient clip name (to avoid re-assigning every frame).
    string currentAmbientClip;

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
        sfxSource.PlayOneShot(clip, sfxVolume);
    }

    // --- Ambient (rain) ---

    void UpdateRainAmbient() {
        float rain = WeatherSystem.instance?.rainAmount ?? 0f;

        // Quadratic curve so volume drops off faster than the linear particle
        // alpha/emission — keeps sound and visuals feeling in sync.
        float vol = rain * rain * ambientVolume;

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
