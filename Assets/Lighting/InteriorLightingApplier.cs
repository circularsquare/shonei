using UnityEngine;
using UnityEngine.SceneManagement;

// Applies SettingsManager.burrowAsBuilding's layer choice to existing enclosed buildings on a
// settings change. The flag is now fixed (the wall-shadows dropdown was retired — burrows always
// render as buildings), so this is mostly belt-and-braces: new structures/mice already read it at
// creation (InteriorLayer.LayerForEnclosed / Animal.RefreshInteriorRendering), and it re-confirms
// the Interior-vs-Default LAYER swap after a ResetToDefaults. (The _InteriorLit / _PointShadows
// shader globals are re-read every frame by LightFeature, so only the layer needs an explicit apply.)
//
// Per-scene self-bootstrap, mirroring BackgroundVisibility: SettingsManager is a per-scene
// singleton, so a fresh instance subscribes to each scene's manager. No scene wiring needed.
// In scenes without a StructController/AnimalController (the menu) Apply is a no-op.
public class InteriorLightingApplier : MonoBehaviour {
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap() {
        EnsureInstance();
        // -= then += guarantees exactly one subscription even with Reload Domain off, where a
        // static event subscription would otherwise persist and stack across play sessions.
        SceneManager.sceneLoaded -= OnSceneLoadedStatic;
        SceneManager.sceneLoaded += OnSceneLoadedStatic;
    }

    static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode) => EnsureInstance();

    static void EnsureInstance() {
        if (FindObjectOfType<InteriorLightingApplier>() != null) return;
        new GameObject("InteriorLightingApplier").AddComponent<InteriorLightingApplier>();
    }

    void OnEnable()  { if (SettingsManager.instance != null) SettingsManager.instance.OnChanged += Apply; }
    void OnDisable() { if (SettingsManager.instance != null) SettingsManager.instance.OnChanged -= Apply; }

    void Apply() {
        int layer = InteriorLayer.LayerForEnclosed();
        if (layer < 0) return; // missing 'Interior' layer already logged by InteriorLayer

        var sc = StructController.instance;
        if (sc != null) {
            foreach (Structure s in sc.GetStructures())
                if (s != null && s.go != null && s.structType.enclosed)
                    InteriorLayer.SetSpriteLayers(s.go, layer);
        }

        var ac = AnimalController.instance;
        if (ac != null && ac.animals != null) {
            foreach (Animal a in ac.animals)
                if (a != null) a.RefreshInteriorRendering();
        }
    }
}
