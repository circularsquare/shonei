using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Tools → Convert Sprites-Default → Sprite-Lit-Default
// Swaps every SpriteRenderer in the open scene that still uses the legacy
// Sprites-Default material over to URP's Sprite-Lit-Default, without
// touching anything else on the component. Supports Undo.
public static class MaterialConverter {
    [MenuItem("Tools/Convert Sprites-Default → Sprite-Lit-Default (Scene)")]
    static void ConvertScene() {
        Material litMat = FindOrCreateLitMaterial();
        if (litMat == null) return;

        int count = 0;
        // includeInactive=true catches disabled GameObjects too
        foreach (SpriteRenderer sr in Object.FindObjectsOfType<SpriteRenderer>(true)) {
            if (sr.sharedMaterial == null || sr.sharedMaterial.name != "Sprites-Default") continue;
            Undo.RecordObject(sr, "Convert to Sprite-Lit-Default");
            sr.sharedMaterial = litMat;
            EditorUtility.SetDirty(sr);
            count++;
        }

        if (count > 0)
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[MaterialConverter] Converted {count} SpriteRenderer(s) to Sprite-Lit-Default.");
    }

    static Material FindOrCreateLitMaterial() {
        // Prefer an existing asset in the project
        foreach (string guid in AssetDatabase.FindAssets("Sprite-Lit-Default t:Material")) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null && mat.shader != null && mat.shader.name.Contains("Sprite-Lit-Default"))
                return mat;
        }

        // Not found — create one from the URP shader
        Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default");
        if (shader == null) {
            Debug.LogError("[MaterialConverter] Shader 'Universal Render Pipeline/2D/Sprite-Lit-Default' not found. " +
                           "Make sure your URP asset uses the 2D Renderer.");
            return null;
        }

        Material newMat = new Material(shader) { name = "Sprite-Lit-Default" };
        System.IO.Directory.CreateDirectory("Assets/Materials");
        AssetDatabase.CreateAsset(newMat, "Assets/Materials/Sprite-Lit-Default.mat");
        AssetDatabase.SaveAssets();
        Debug.Log("[MaterialConverter] Created Assets/Materials/Sprite-Lit-Default.mat");
        return newMat;
    }
}
