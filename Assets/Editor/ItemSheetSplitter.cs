using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;
using SysDir  = System.IO.Directory;

// Splits item sprite sheets into individual files in the item's sprite folder.
//
// Sheet format: 2 columns × N rows, each cell is CellSize × CellSize pixels.
// Cells are read left-to-right, top-to-bottom (row 0 = top of image).
// Transparent cells are skipped (no file written).
//
// Sheet layout (current):
//   col 0    col 1
//   icon     floor     ← row 0
//   smid     qmid      ← row 1
//
// To add future variants, uncomment rows in the Slots array below.
//
// Source:  Assets/Resources/Sheets/{itemName}.png
// Output:  Assets/Resources/Sprites/Items/split/{itemName}/{slotName}.png
//
// Usage:
//   Tools → Split All Item Sheets       — processes every sheet in Sheets/
//   Right-click sheet → Split Item Sheet — processes selected sheet(s)
public static class ItemSheetSplitter {
    const int CellSize   = 16;
    const string SheetsFolder = "Assets/Resources/Sprites/Items/Sheets";
    const string ItemsFolder  = "Assets/Resources/Sprites/Items/split";

    // (row, col, outputFileName, cropSize)  cropSize <= CellSize; top-left of cell is used.
    // Quarter sprites (q*) use cropSize=6 so that when placed at ±0.25 Unity units (±4 px at PPU=16),
    // the sprite edges land on exact pixel boundaries (4±3 = 1 and 7). 7×7 would give ±3.5 px = misaligned.
    static readonly (int row, int col, string name, int cropSize)[] Slots = {
        (0, 0, "icon",  CellSize),
        (0, 1, "floor", CellSize),
        (1, 0, "slow",  CellSize),
        (1, 1, "smid",  CellSize),
        (1, 2, "shigh", CellSize),
        (2, 0, "qlow",  6),
        (2, 1, "qmid",  6),
        (2, 2, "qhigh", 6),
    };

    // ── batch: all sheets in Sheets/ ─────────────────────────────────────────
    [MenuItem("Tools/Split All Item Sheets")]
    internal static void SplitAll() {
        SplitFolders(new[] { SheetsFolder });
    }

    // ── folder: right-click a folder to split all sheets in it ───────────────
    [MenuItem("Assets/Split Item Sheets in Folder", validate = true)]
    static bool ValidateFolder() {
        foreach (Object o in Selection.objects) {
            string path = AssetDatabase.GetAssetPath(o);
            if (AssetDatabase.IsValidFolder(path)) return true;
        }
        return false;
    }

    [MenuItem("Assets/Split Item Sheets in Folder")]
    static void SplitFolder() {
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
            if (path.EndsWith("_n.png")) continue;       // skip generated normal maps in Sheets/
            string itemName = SysPath.GetFileNameWithoutExtension(path);
            SplitSheet(path, itemName, written);
            count++;
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[SheetSplitter] Done — split {count} sheet(s), wrote {written.Count} sprite(s).");
    }

    // ── single: right-click a sheet texture ──────────────────────────────────
    [MenuItem("Assets/Split Item Sheet", validate = true)]
    static bool ValidateSingle() {
        foreach (Object o in Selection.objects) {
            if (o is Texture2D) {
                string path = AssetDatabase.GetAssetPath(o);
                if (path.StartsWith(SheetsFolder)) return true;
            }
        }
        return false;
    }

    [MenuItem("Assets/Split Item Sheet")]
    static void SplitSelected() {
        List<string> written = new List<string>();
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D)) continue;
            string path = AssetDatabase.GetAssetPath(o);
            if (!path.StartsWith(SheetsFolder)) continue;
            if (path.EndsWith("_n.png")) continue;       // skip generated normal maps
            string itemName = SysPath.GetFileNameWithoutExtension(path);
            SplitSheet(path, itemName, written);
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[SheetSplitter] Done — wrote {written.Count} sprite(s).");
    }

    // ── core ─────────────────────────────────────────────────────────────────
    static void SplitSheet(string sheetPath, string itemName, List<string> written) {
        TextureImporter imp = AssetImporter.GetAtPath(sheetPath) as TextureImporter;
        if (imp == null) { Debug.LogWarning($"[SheetSplitter] No importer for {sheetPath}"); return; }

        // Temporarily enable CPU read
        bool wasReadable = imp.isReadable;
        if (!wasReadable) { imp.isReadable = true; imp.SaveAndReimport(); }

        Texture2D sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(sheetPath);
        if (sheet == null) {
            if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
            Debug.LogWarning($"[SheetSplitter] Could not load {sheetPath}");
            return;
        }

        string outDir = SysPath.Combine(ItemsFolder, itemName).Replace('\\', '/');
        SysDir.CreateDirectory(outDir);

        foreach (var (row, col, slotName, cropSize) in Slots) {
            int px = col * CellSize;
            // Texture2D pixel coords have y=0 at bottom; rows in the sheet are top-to-bottom.
            // cropSize crops from the top-left of the cell, so offset py up by (CellSize - cropSize).
            int py = sheet.height - (row + 1) * CellSize + (CellSize - cropSize);

            if (px + cropSize > sheet.width || py < 0) {
                Debug.LogWarning($"[SheetSplitter] {itemName}: slot '{slotName}' out of bounds (sheet {sheet.width}×{sheet.height}), skipping.");
                continue;
            }

            Color[] pixels = sheet.GetPixels(px, py, cropSize, cropSize);

            // Skip fully-transparent slots (empty cells in the sheet)
            bool hasContent = false;
            foreach (var p in pixels) { if (p.a > 0) { hasContent = true; break; } }
            if (!hasContent) continue;

            Texture2D cell = new Texture2D(cropSize, cropSize, TextureFormat.RGBA32, false);
            cell.SetPixels(pixels);
            cell.Apply();

            string outPath = SysPath.Combine(outDir, slotName + ".png").Replace('\\', '/');
            SysFile.WriteAllBytes(outPath, cell.EncodeToPNG());
            Object.DestroyImmediate(cell);

            written.Add(outPath);
            Debug.Log($"[SheetSplitter] Wrote: {outPath}");
        }

        if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
    }

    // ── combo: split sheets then generate normal maps for everything ───────
    [MenuItem("Tools/Split Sheets + Generate Normal Maps")]
    static void SplitThenGenerateNormals() {
        SplitAll();
        SpriteNormalMapGenerator.GenerateAll();
    }

    // Apply import settings to newly written sprites to match expected game settings
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
