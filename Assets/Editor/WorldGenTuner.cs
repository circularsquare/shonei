using System.IO;
using UnityEditor;
using UnityEngine;

// Editor-side helpers for WorldGenConfig:
//   - Custom inspector adds "Regenerate (same seed)" / "Regenerate (new seed)"
//     buttons at the top of the asset's inspector so tuning + regen live in
//     one window. Tuning happens via the default ScriptableObject inspector
//     below (sliders defined via [Range] attributes on WorldGenConfig).
//   - Menu item creates the canonical asset at Resources/WorldGenConfig.asset
//     if missing. Right-click → Create → Shonei → WorldGen Config also works
//     (via the [CreateAssetMenu] on the SO), but only into the currently-
//     selected folder.
[CustomEditor(typeof(WorldGenConfig))]
public class WorldGenConfigEditor : Editor {
    public override void OnInspectorGUI() {
        // Regen controls — only meaningful in Play mode (SaveSystem.instance + ClearWorld).
        using (new EditorGUI.DisabledScope(!Application.isPlaying)) {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Regenerate (same seed)")) RegenerateSameSeed();
            if (GUILayout.Button("Regenerate (new seed)"))  RegenerateNewSeed();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"Last seed: {WorldController.lastGeneratedSeed}");
        }
        if (!Application.isPlaying) {
            EditorGUILayout.HelpBox("Regenerate buttons require Play mode.", MessageType.Info);
        }
        EditorGUILayout.Space();
        // Default inspector renders all SO fields with their [Range] / [Header] /
        // [Tooltip] attributes. Edits write directly to the asset and persist.
        DrawDefaultInspector();
    }

    static void RegenerateSameSeed() {
        if (SaveSystem.instance == null) return;
        WorldController.pendingSeedOverride = WorldController.lastGeneratedSeed != 0
            ? WorldController.lastGeneratedSeed
            : (int?)null;
        SaveSystem.instance.LoadDefault();
    }

    static void RegenerateNewSeed() {
        if (SaveSystem.instance == null) return;
        WorldController.pendingSeedOverride = Random.Range(1, 100000);
        SaveSystem.instance.LoadDefault();
    }

    // Creates the live asset at Assets/Resources/WorldGenConfig.asset if it
    // doesn't already exist there, then selects it in the Project view. Safe
    // to run multiple times (no-op when the asset is already present).
    [MenuItem("Window/Shonei/Create WorldGen Config asset")]
    public static void CreateConfigAsset() {
        const string folder = "Assets/Resources";
        const string path = folder + "/WorldGenConfig.asset";

        if (!Directory.Exists(folder)) {
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
        }

        WorldGenConfig existing = AssetDatabase.LoadAssetAtPath<WorldGenConfig>(path);
        if (existing != null) {
            Selection.activeObject = existing;
            EditorGUIUtility.PingObject(existing);
            return;
        }

        WorldGenConfig asset = ScriptableObject.CreateInstance<WorldGenConfig>();
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
        Debug.Log($"Created WorldGenConfig at {path}");
    }
}
