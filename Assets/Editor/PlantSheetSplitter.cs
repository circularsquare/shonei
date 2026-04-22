using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;
using SysDir  = System.IO.Directory;

// Splits plant sprite sheets into individual growth-stage files.
//
// Sheet format: 1 row of CellSize × CellSize cells, read left-to-right.
// Number of cells is derived from sheet width ÷ CellSize — e.g. a 64×16 sheet
// splits into 4 stages (g0..g3), an 80×16 sheet into 5 (g0..g4, for multi-tile
// plants like bamboo that need a stalk-continuation sprite).
// Transparent cells are skipped (no file written).
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

        // Column count derived from sheet width. Only the top row is read —
        // multi-row sheets aren't supported yet (no plant needs them).
        int numCols = sheet.width / CellSize;
        if (numCols <= 0) {
            Debug.LogWarning($"[PlantSheetSplitter] {sheetPath} is narrower than one cell ({CellSize}px) — skipping");
        }
        for (int col = 0; col < numCols; col++) {
            string slotName = "g" + col;
            int px = col * CellSize;
            int py = sheet.height - CellSize;

            if (py < 0) continue;

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
