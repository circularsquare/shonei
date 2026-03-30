using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;
using SysDir  = System.IO.Directory;

// Splits plant sprite sheets into individual growth-stage files.
//
// Sheet format: 4 columns × 1 row, each cell is CellSize × CellSize pixels.
// The sheet is 64 wide × 16 high. Cells are read left-to-right.
// Transparent cells are skipped (no file written).
//
// Sheet layout:
//   col 0    col 1    col 2    col 3
//   g0       g1       g2       g3        ← growth stages
//
// Source:  Assets/Resources/Sprites/Plants/Sheets/{plantName}.png
// Output:  Assets/Resources/Sprites/Plants/Split/{plantName}/g0.png  etc.
//
// Usage:
//   Tools → Split All Plant Sheets          — processes every sheet in Sheets/
//   Right-click sheet → Split Plant Sheet    — processes selected sheet(s)
//   Right-click folder → Split Plant Sheets in Folder
public static class PlantSheetSplitter {
    const int CellSize     = 16;
    const string SheetsFolder = "Assets/Resources/Sprites/Plants/Sheets";
    const string SplitFolder  = "Assets/Resources/Sprites/Plants/Split";

    static readonly (int row, int col, string name)[] Slots = {
        (0, 0, "g0"),
        (0, 1, "g1"),
        (0, 2, "g2"),
        (0, 3, "g3"),
    };

    // ── batch: all sheets in Sheets/ ─────────────────────────────────────────
    [MenuItem("Tools/Split All Plant Sheets")]
    internal static void SplitAll() {
        SplitFolders(new[] { SheetsFolder });
    }

    // ── folder: right-click a folder to split all sheets in it ───────────────
    [MenuItem("Assets/Split Plant Sheets in Folder", validate = true)]
    static bool ValidateFolder() {
        foreach (Object o in Selection.objects) {
            string path = AssetDatabase.GetAssetPath(o);
            if (AssetDatabase.IsValidFolder(path)) return true;
        }
        return false;
    }

    [MenuItem("Assets/Split Plant Sheets in Folder")]
    static void SplitSelectedFolder() {
        var folders = new List<string>();
        foreach (Object o in Selection.objects) {
            string path = AssetDatabase.GetAssetPath(o);
            if (AssetDatabase.IsValidFolder(path)) folders.Add(path);
        }
        SplitFolders(folders.ToArray());
    }

    static void SplitFolders(string[] folders) {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", folders);
        int count = 0;
        List<string> written = new List<string>();
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith("_n.png")) continue;
            string plantName = SysPath.GetFileNameWithoutExtension(path);
            SplitSheet(path, plantName, written);
            count++;
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[PlantSheetSplitter] Done — split {count} sheet(s), wrote {written.Count} sprite(s).");
    }

    // ── single: right-click a sheet texture ──────────────────────────────────
    [MenuItem("Assets/Split Plant Sheet", validate = true)]
    static bool ValidateSingle() {
        foreach (Object o in Selection.objects) {
            if (o is Texture2D) {
                string path = AssetDatabase.GetAssetPath(o);
                if (path.StartsWith(SheetsFolder)) return true;
            }
        }
        return false;
    }

    [MenuItem("Assets/Split Plant Sheet")]
    static void SplitSelected() {
        List<string> written = new List<string>();
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D)) continue;
            string path = AssetDatabase.GetAssetPath(o);
            if (!path.StartsWith(SheetsFolder)) continue;
            if (path.EndsWith("_n.png")) continue;
            string plantName = SysPath.GetFileNameWithoutExtension(path);
            SplitSheet(path, plantName, written);
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[PlantSheetSplitter] Done — wrote {written.Count} sprite(s).");
    }

    // ── core ─────────────────────────────────────────────────────────────────
    static void SplitSheet(string sheetPath, string plantName, List<string> written) {
        TextureImporter imp = AssetImporter.GetAtPath(sheetPath) as TextureImporter;
        if (imp == null) { Debug.LogWarning($"[PlantSheetSplitter] No importer for {sheetPath}"); return; }

        bool wasReadable = imp.isReadable;
        if (!wasReadable) { imp.isReadable = true; imp.SaveAndReimport(); }

        Texture2D sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(sheetPath);
        if (sheet == null) {
            if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
            Debug.LogWarning($"[PlantSheetSplitter] Could not load {sheetPath}");
            return;
        }

        string outDir = SysPath.Combine(SplitFolder, plantName).Replace('\\', '/');
        SysDir.CreateDirectory(outDir);

        foreach (var (row, col, slotName) in Slots) {
            int px = col * CellSize;
            int py = sheet.height - (row + 1) * CellSize;

            if (px + CellSize > sheet.width || py < 0) continue;

            Color[] pixels = sheet.GetPixels(px, py, CellSize, CellSize);

            // Skip fully-transparent cells
            bool hasContent = false;
            foreach (var p in pixels) { if (p.a > 0) { hasContent = true; break; } }
            if (!hasContent) continue;

            Texture2D cell = new Texture2D(CellSize, CellSize, TextureFormat.RGBA32, false);
            cell.SetPixels(pixels);
            cell.Apply();

            string outPath = SysPath.Combine(outDir, slotName + ".png").Replace('\\', '/');
            SysFile.WriteAllBytes(outPath, cell.EncodeToPNG());
            Object.DestroyImmediate(cell);

            written.Add(outPath);
            Debug.Log($"[PlantSheetSplitter] Wrote: {outPath}");
        }

        if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
    }

    // ── combo: split plant sheets then generate normal maps ──────────────────
    [MenuItem("Tools/Split Plant Sheets + Generate Normal Maps")]
    static void SplitThenGenerateNormals() {
        SplitAll();
        SpriteNormalMapGenerator.GenerateAll();
    }

    static void ApplyImportSettings(List<string> paths) {
        foreach (string path in paths) {
            AssetDatabase.ImportAsset(path);
            TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) continue;
            imp.textureType        = TextureImporterType.Sprite;
            imp.spritePixelsPerUnit = 16;
            imp.filterMode         = FilterMode.Point;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.wrapMode           = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }
    }
}
