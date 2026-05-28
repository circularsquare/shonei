using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// ── Aseprite Resources Guard ───────────────────────────────────────────
// .ase / .aseprite files generate sub-sprites via Unity's Aseprite Importer.
// When they live under Assets/Resources/, those sub-sprites collide with
// canonical .png exports — Resources.LoadAll<Sprite>(path) returns both,
// and the rendered sprite becomes whichever one Unity hands back first.
// Symptom: a 3×7 px micropixel where the .png used to render.
//
// Policy: .ase files are editing source, not runtime assets. They belong
// outside Resources/. This guard auto-moves any .ase under Resources/ to
// a mirror path under Assets/AsepriteSources/ on import, preserving the
// folder structure so the user can find them again. Pair with the .ase
// filter in GameplayAtlasBuilder which prevents .ase sprites from reaching
// runtime atlases (defense in depth).
public class AsepriteResourcesGuard : AssetPostprocessor
{
    const string SourceRoot = "Assets/Resources/";
    const string TargetRoot = "Assets/AsepriteSources/";

    static void OnPostprocessAllAssets(string[] imported, string[] deleted,
                                       string[] moved, string[] movedFrom)
    {
        // Both new imports and assets moved INTO Resources need handling —
        // a drag-drop from outside fires `imported`, a move from another
        // project folder fires `moved`.
        foreach (var path in imported) TryRelocate(path);
        foreach (var path in moved)    TryRelocate(path);
    }

    [MenuItem("Tools/Move .ase Out of Resources", priority = 500)]
    public static void ScanResourcesNow()
    {
        if (!AssetDatabase.IsValidFolder(SourceRoot)) {
            Debug.Log($"[AsepriteResourcesGuard] No {SourceRoot} folder — nothing to scan.");
            return;
        }
        var hits = new List<string>();
        // FindAssets on a Unity-imported scripted importer matches "t:Object"
        // for any imported file. Scan by filesystem to also catch unimported
        // strays.
        var fullRoot = Application.dataPath + "/Resources";
        if (Directory.Exists(fullRoot)) {
            foreach (var f in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)) {
                if (!IsAse(f)) continue;
                var rel = "Assets/Resources" + f.Substring(fullRoot.Length).Replace('\\', '/');
                hits.Add(rel);
            }
        }
        int moved = 0;
        foreach (var p in hits) if (TryRelocate(p)) moved++;
        Debug.Log($"[AsepriteResourcesGuard] Scan complete. {moved} of {hits.Count} .ase file(s) relocated.");
    }

    static bool TryRelocate(string path)
    {
        if (!IsAse(path)) return false;
        if (!path.StartsWith(SourceRoot, System.StringComparison.OrdinalIgnoreCase)) return false;

        var relative = path.Substring(SourceRoot.Length);
        var dst = TargetRoot + relative;

        EnsureFolderExists(System.IO.Path.GetDirectoryName(dst).Replace('\\', '/'));

        // GenerateUniqueAssetPath avoids the error path when a stale .ase
        // already exists at the destination from a previous relocation.
        if (AssetDatabase.LoadAssetAtPath<Object>(dst) != null)
            dst = AssetDatabase.GenerateUniqueAssetPath(dst);

        string err = AssetDatabase.MoveAsset(path, dst);
        if (string.IsNullOrEmpty(err)) {
            Debug.LogWarning(
                $"[AsepriteResourcesGuard] Moved Aseprite source out of Resources: {path} -> {dst}. " +
                "Aseprite files don't belong in Resources/ — they shadow .png sprites loaded by Resources.LoadAll. " +
                "Keep .ase under Assets/AsepriteSources/ (or anywhere outside Resources/).");
            return true;
        }
        Debug.LogError($"[AsepriteResourcesGuard] Failed to move {path} -> {dst}: {err}");
        return false;
    }

    static bool IsAse(string path)
    {
        return path.EndsWith(".ase", System.StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".aseprite", System.StringComparison.OrdinalIgnoreCase);
    }

    static void EnsureFolderExists(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (AssetDatabase.IsValidFolder(folder)) return;
        var parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolderExists(parent);
        var name = System.IO.Path.GetFileName(folder);
        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(name))
            AssetDatabase.CreateFolder(parent, name);
    }
}
