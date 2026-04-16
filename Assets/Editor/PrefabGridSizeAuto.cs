using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Auto-sets the move-tool snap increment when entering/leaving Prefab Mode so
// dragging snaps at the right pixel scale:
//   - Main scene (default): 1 — we nudge UI overlays here more than world art.
//   - UI prefab (root has RectTransform): 1 — refPPU=160 canvas preview renders
//     1 world unit ≈ 1 canvas reference pixel, so snap 1 = one UI pixel.
//   - World prefab (sprite-renderer art): 0.0625 — PPU=16 sprites, so 1 pixel
//     = 1/16 world units.
//
// Only the move-tool snap increment (EditorSnapSettings.move) is touched — it's
// the public API that actually affects dragging behaviour. The Scene view's
// visual grid mesh is driven by internal state we couldn't reliably update via
// reflection; adjust it manually via the Grid and Snap overlay if you need the
// visual to match.
[InitializeOnLoad]
static class PrefabGridSizeAuto {
    const float UIGridSize        = 1f;
    const float WorldGridSize     = 0.0625f;
    const float MainSceneGridSize = 1f;

    static PrefabGridSizeAuto() {
        PrefabStage.prefabStageOpened  += OnPrefabStageOpened;
        PrefabStage.prefabStageClosing += OnPrefabStageClosing;
    }

    static void OnPrefabStageOpened(PrefabStage stage) {
        if (stage == null || stage.prefabContentsRoot == null) return;
        bool isUI = stage.prefabContentsRoot.GetComponent<RectTransform>() != null;
        SetSnap(isUI ? UIGridSize : WorldGridSize);
    }

    static void OnPrefabStageClosing(PrefabStage _) {
        SetSnap(MainSceneGridSize);
    }

    static void SetSnap(float size) {
        EditorSnapSettings.move = new Vector3(size, size, size);
    }
}
