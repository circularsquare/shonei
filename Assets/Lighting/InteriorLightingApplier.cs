using UnityEngine;
using UnityEngine.SceneManagement;

// Re-applies SettingsManager.interiorMode's layer choice to existing enclosed buildings when
// the mode changes at runtime. The mode's two shader globals (_InteriorLit / _PointShadows) are
// re-read every frame by LightFeature, so only the Interior-vs-Default LAYER swap needs an
// explicit re-apply — that's what flips a burrow between its own interior tier and the normal
// building tier (the BurrowAsBuilding comparison mode). New structures/mice read the mode at
// creation (InteriorLayer.LayerForEnclosed / Animal.RefreshInteriorRendering), so this only
// matters for live toggling of an already-built world.
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
