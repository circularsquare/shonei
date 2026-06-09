using UnityEngine;
using UnityEngine.SceneManagement;

// Applies SettingsManager.hideBackground: when on, hides every decorative sky layer
// (clouds, hills, gradient, stars, haze — all SkyLayerBase) so the scene falls back to
// the camera's flat clear color. Works in any scene that has sky layers (menu + game).
//
// Per-scene self-bootstrap, mirroring UITextRuntimeStyle: SettingsManager is a per-scene
// singleton, so a fresh instance is created on each scene load to subscribe to that
// scene's manager and re-apply to that scene's layers. No scene wiring needed.
public class BackgroundVisibility : MonoBehaviour {
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap() {
        EnsureInstance();
        // -= then += guarantees exactly one subscription even with Reload Domain off, where
        // this static event subscription would otherwise persist and stack across play sessions.
        SceneManager.sceneLoaded -= OnSceneLoadedStatic;
        SceneManager.sceneLoaded += OnSceneLoadedStatic;
    }

    static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode) => EnsureInstance();

    static void EnsureInstance() {
        if (FindObjectOfType<BackgroundVisibility>() != null) return;
        new GameObject("BackgroundVisibility").AddComponent<BackgroundVisibility>();
    }

    void OnEnable()  { if (SettingsManager.instance != null) SettingsManager.instance.OnChanged += Apply; }
    void OnDisable() { if (SettingsManager.instance != null) SettingsManager.instance.OnChanged -= Apply; }
    void Start() { Apply(); }

    void Apply() {
        bool hide = SettingsManager.instance != null && SettingsManager.instance.hideBackground;
        // includeInactive: true so we can re-enable layers that a previous "hide" turned off.
        foreach (SkyLayerBase layer in FindObjectsOfType<SkyLayerBase>(true))
            if (layer != null) layer.gameObject.SetActive(!hide);
    }
}
