using UnityEditor;
using UnityEngine;

// Editor-side helpers for WorldGenConfig: custom inspector adds "Regenerate
// (same seed)" / "Regenerate (new seed)" buttons at the top of the asset's
// inspector so tuning + regen live in one window. Tuning happens via the
// default ScriptableObject inspector below (sliders defined via [Range]
// attributes on WorldGenConfig). Asset creation: right-click → Create →
// Shonei → WorldGen Config (via [CreateAssetMenu] on the SO).
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

    // Open the WorldGenConfig asset in the inspector from anywhere. Saves
    // hunting through the project tree — the asset can live in any Resources
    // folder and this finds it by type via AssetDatabase.
    [MenuItem("Tools/Open WorldGen Config", priority = 900)]
    static void OpenWorldGenConfig() {
        var guids = AssetDatabase.FindAssets("t:WorldGenConfig");
        if (guids.Length == 0) {
            Debug.LogError("WorldGenConfig: no asset found in project. Create one via Assets → Create → Shonei → WorldGen Config and place it in a Resources folder.");
            return;
        }
        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
        var asset = AssetDatabase.LoadAssetAtPath<WorldGenConfig>(path);
        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
    }

}
