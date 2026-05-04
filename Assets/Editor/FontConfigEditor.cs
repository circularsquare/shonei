using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Editor support for FontConfig: custom inspector "Apply to All" button and a
// Tools/Apply Font Config menu item. Both invoke ApplyEverywhere, which:
//   1. Walks all TMP_Text components in open scenes, sets font + fontSize.
//   2. Walks all prefabs in the project (loaded via PrefabUtility), updates
//      their TMP_Text descendants, saves the prefabs back to disk.
//
// Prefab pass uses LoadPrefabContents + SaveAsPrefabAsset (live API) rather
// than direct YAML edits — safe with unsaved editor work.
[CustomEditor(typeof(FontConfig))]
public class FontConfigEditor : Editor {
    public override void OnInspectorGUI() {
        DrawDefaultInspector();
        EditorGUILayout.Space();
        if (GUILayout.Button("Apply to All Scenes & Prefabs")) {
            ApplyEverywhere((FontConfig)target);
        }
    }

    [MenuItem("Tools/Apply Font Config")]
    static void ApplyMenu() {
        var cfg = Resources.Load<FontConfig>("FontConfig");
        if (cfg == null) { Debug.LogError("FontConfig: Assets/Resources/FontConfig.asset not found"); return; }
        ApplyEverywhere(cfg);
    }

    static void ApplyEverywhere(FontConfig cfg) {
        if (cfg.font == null) {
            Debug.LogError("FontConfig: font field is null — assign a TMP_FontAsset before applying.");
            return;
        }

        int sceneCount = 0;
        var open = Resources.FindObjectsOfTypeAll(typeof(TMP_Text));
        foreach (var o in open) {
            var t = (TMP_Text)o;
            // Skip prefab assets (scene check) and disabled-but-not-destroyed objects in
            // closed scenes. The prefab pass below handles prefab assets directly.
            if (!t.gameObject.scene.IsValid() || !t.gameObject.scene.isLoaded) continue;
            if (FontConfig.Apply(t)) {
                EditorUtility.SetDirty(t);
                sceneCount++;
            }
        }
        if (sceneCount > 0) EditorSceneManager.SaveOpenScenes();

        int prefabCount = 0;
        string[] guids = AssetDatabase.FindAssets("t:Prefab");
        foreach (var guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            // Skip TMP plugin example prefabs and other 3rd-party content.
            if (path.Contains("/TextMesh Pro/")) continue;

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try {
                bool changed = false;
                var tmps = root.GetComponentsInChildren<TMP_Text>(true);
                foreach (var t in tmps) {
                    if (FontConfig.Apply(t)) { changed = true; prefabCount++; }
                }
                if (changed) PrefabUtility.SaveAsPrefabAsset(root, path);
            } finally {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        Debug.Log($"FontConfig applied: {sceneCount} scene components, {prefabCount} prefab components.");
    }
}
