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

}
