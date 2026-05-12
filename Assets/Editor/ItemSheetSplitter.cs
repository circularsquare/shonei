using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text;
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
// Up-to-date check:
//   Each sheet's importer userData carries `itemSplitCacheKey=<md5>` after a
//   successful split. The key hashes source PNG mtime + the Slots config, so
//   any sheet edit OR a code change to Slots invalidates cached sheets. The
//   batch "All" menus honor this cache; "(Force)" ignores it. Single-asset
//   menu always forces. If a user deletes individual split files but leaves
//   the output folder intact, the cache won't notice — use single-asset force
//   or wipe the folder. Mirrors SpriteNormalMapGenerator's caching shape.
//
// Usage:
//   Tools → Split All Item Sheets         — processes every sheet (cache-aware)
//   Tools → Split All Item Sheets (Force) — ignores cache
//   Right-click sheet → Split Item Sheet  — processes selected sheet(s), force
public static class ItemSheetSplitter {
    const int CellSize   = 16;
    const string SheetsFolder = "Assets/Resources/Sprites/Items/Sheets";
    const string ItemsFolder  = "Assets/Resources/Sprites/Items/split";
    const string CacheKey     = "itemSplitCacheKey";

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
        SplitFolders(new[] { SheetsFolder }, force: false);
    }

    [MenuItem("Tools/Split All Item Sheets (Force)")]
    internal static void SplitAllForce() {
        SplitFolders(new[] { SheetsFolder }, force: true);
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
        SplitFolders(folders.ToArray(), force: false);
    }

    static void SplitFolders(string[] folders, bool force) {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", folders);
        int processed = 0, skipped = 0;
        List<string> written = new List<string>();
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith("_n.png")) continue;       // skip generated normal maps in Sheets/
            string itemName = SysPath.GetFileNameWithoutExtension(path);
            if (SplitSheet(path, itemName, written, force)) processed++;
            else                                            skipped++;
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[SheetSplitter] Done — processed {processed}, skipped {skipped} up-to-date, wrote {written.Count} sprite(s).{(force ? " (force)" : "")}");
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
            SplitSheet(path, itemName, written, force: true);
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[SheetSplitter] Done — wrote {written.Count} sprite(s).");
    }

    // ── core ─────────────────────────────────────────────────────────────────
    // Returns true if the sheet was processed, false if skipped (up-to-date).
    static bool SplitSheet(string sheetPath, string itemName, List<string> written, bool force) {
        TextureImporter imp = AssetImporter.GetAtPath(sheetPath) as TextureImporter;
        if (imp == null) { Debug.LogWarning($"[SheetSplitter] No importer for {sheetPath}"); return false; }

        if (!force && IsUpToDate(sheetPath, itemName, imp)) return false;

        // Temporarily enable CPU read
        bool wasReadable = imp.isReadable;
        if (!wasReadable) { imp.isReadable = true; imp.SaveAndReimport(); }

        Texture2D sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(sheetPath);
        if (sheet == null) {
            if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
            Debug.LogWarning($"[SheetSplitter] Could not load {sheetPath}");
            return false;
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

        // Stamp cache key on success. Folds into the trailing reimport when
        // wasReadable was false; otherwise costs one extra SaveAndReimport.
        SetUserDataFlag(imp, CacheKey, ComputeCacheKey(sheetPath));
        if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
        else              { imp.SaveAndReimport(); }
        return true;
    }

    // ── up-to-date check ─────────────────────────────────────────────────────
    // Inputs that should invalidate a cached split:
    //   - source PNG content (mtime proxy)
    //   - Slots config (so adding/removing slots in code re-splits everything)
    //   - output folder existence (user may have deleted the whole folder)
    // Individual missing output files are NOT detected — use the single-asset
    // "Split Item Sheet" menu (force) or wipe the folder to recover.
    static bool IsUpToDate(string sheetPath, string itemName, TextureImporter imp) {
        string stored = GetUserDataValue(imp, CacheKey);
        if (string.IsNullOrEmpty(stored)) return false;
        if (stored != ComputeCacheKey(sheetPath)) return false;
        string outDir = SysPath.Combine(ItemsFolder, itemName).Replace('\\', '/');
        if (!SysDir.Exists(outDir)) return false;
        return true;
    }

    static string ComputeCacheKey(string sheetPath) {
        var sb = new StringBuilder();
        sb.Append(SysFile.GetLastWriteTimeUtc(sheetPath).Ticks);
        sb.Append('|').Append(CellSize);
        foreach (var (row, col, name, cropSize) in Slots) {
            sb.Append('|').Append(row).Append(',').Append(col)
              .Append(',').Append(name).Append(',').Append(cropSize);
        }
        return Md5(sb.ToString());
    }

    // ── combo: split all sheets then generate normal maps for everything ────
    [MenuItem("Tools/Split Sheets + Generate Normal Maps")]
    static void SplitThenGenerateNormals() {
        SplitAll();
        PlantSheetSplitter.SplitAll();
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

    // ── userData helpers ─────────────────────────────────────────────────────
    // Importer userData carries semicolon-separated key=value pairs. Same
    // format SpriteNormalMapGenerator uses, so the splitter's `itemSplitCacheKey`
    // can coexist with the generator's `normalsCacheKey` on the same sheet.
    static string GetUserDataValue(TextureImporter imp, string key) {
        if (imp == null || string.IsNullOrEmpty(imp.userData)) return null;
        foreach (string pair in imp.userData.Split(';')) {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair.Substring(0, eq).Trim() == key) return pair.Substring(eq + 1).Trim();
        }
        return null;
    }

    static void SetUserDataFlag(TextureImporter imp, string key, string value) {
        var pairs = new List<string>();
        bool replaced = false;
        if (!string.IsNullOrEmpty(imp.userData)) {
            foreach (string pair in imp.userData.Split(';')) {
                int eq = pair.IndexOf('=');
                if (eq < 0) { if (!string.IsNullOrWhiteSpace(pair)) pairs.Add(pair); continue; }
                string k = pair.Substring(0, eq).Trim();
                if (k == key) { pairs.Add($"{key}={value}"); replaced = true; }
                else          { pairs.Add(pair); }
            }
        }
        if (!replaced) pairs.Add($"{key}={value}");
        imp.userData = string.Join(";", pairs);
    }

    static string Md5(string s) {
        using (var m = System.Security.Cryptography.MD5.Create()) {
            byte[] b = m.ComputeHash(Encoding.UTF8.GetBytes(s));
            return System.Convert.ToBase64String(b);
        }
    }
}
