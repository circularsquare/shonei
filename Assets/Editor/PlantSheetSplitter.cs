using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;
using SysDir  = System.IO.Directory;

// Splits plant sprite sheets into individual growth-stage files.
//
// Sheet format: CellSize × CellSize cells in a grid. Read left-to-right within
// each row, top-to-bottom across rows.
//   - 1 row  → row 0 emits g0..g{N-1}.   Single-tile plants and bamboo (which
//              uses g4 as a stalk-continuation sprite for both anchor and
//              non-top extensions).
//   - 2 rows → row 0 (top in image)    emits b0..b{N-1}  — anchor (bottom-tile)
//              row 1 (bottom in image) emits g0..g{N-1}  — upper-tile sprites
//              Lets a plant give its anchor a distinct look (e.g. a flared
//              tree base) while upper trunk segments use a different sprite.
//              Plant.UpdateSprite tries b{i} first and falls back to g{i}.
// Number of cells per row is derived from sheet width ÷ CellSize. Transparent
// cells are skipped (no file written) — for `_sway` mask sheets that means
// trunk-rigid cells must still be authored with alpha=1 (typically pure black,
// since R-channel = 0 = rigid). Fully transparent cells are treated as "no
// mask for this stage" and that stage falls back to vertex-mode at runtime.
//
// Source:  Assets/Resources/Sprites/Plants/Sheets/{plantName}.png
// Output:  Assets/Resources/Sprites/Plants/Split/{plantName}/g0.png  etc.
//          (and b0.png etc. for 2-row sheets)
//
// Sway mask sheets:
//   Source: Assets/Resources/Sprites/Plants/Sheets/{plantName}_sway.png
//   Output: Assets/Resources/Sprites/Plants/Split/{plantName}/g0_sway.png  etc.
//   Same layout as the base sheet — same number of rows/columns, R-channel
//   encodes the sway weight (0 = rigid, 1 = full sway). Output lands inside
//   the base plant's folder with a `_sway` suffix on each cell so
//   SpriteNormalMapGenerator can wire it as a `_SwayMask` secondary on the
//   matching base sprite.
//
// Up-to-date check:
//   Each sheet's importer userData carries `plantSplitCacheKey=<md5>` after a
//   successful split. The key hashes source PNG mtime + CellSize, so any sheet
//   edit invalidates the cache. The batch "All" menus honor the cache;
//   "(Force)" ignores it. Single-asset menu always forces. Individual missing
//   output files are NOT detected — force-regen to recover. Mirrors
//   SpriteNormalMapGenerator's caching shape.
//
// Usage:
//   Tools → Split All Plant Sheets          — processes every sheet (cache-aware)
//   Tools → Split All Plant Sheets (Force)  — ignores cache
//   Right-click sheet → Split Plant Sheet   — processes selected sheet(s), force
//   Right-click folder → Split Plant Sheets in Folder
public static class PlantSheetSplitter {
    const int CellSize     = 16;
    const string SheetsFolder = "Assets/Resources/Sprites/Plants/Sheets";
    const string SplitFolder  = "Assets/Resources/Sprites/Plants/Split";
    const string CacheKey     = "plantSplitCacheKey";

    // ── batch: all sheets in Sheets/ ─────────────────────────────────────────
    [MenuItem("Tools/Split All Plant Sheets")]
    internal static void SplitAll() {
        SplitFolders(new[] { SheetsFolder }, force: false);
    }

    [MenuItem("Tools/Split All Plant Sheets (Force)")]
    internal static void SplitAllForce() {
        SplitFolders(new[] { SheetsFolder }, force: true);
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
        SplitFolders(folders.ToArray(), force: false);
    }

    static void SplitFolders(string[] folders, bool force) {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", folders);
        int processed = 0, skipped = 0;
        List<string> written = new List<string>();
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith("_n.png")) continue;
            // Baker companions — not sprite sheets meant for in-game rendering.
            // Splitting them would pollute Split/ with useless plant folders.
            if (path.EndsWith("_blobs.png")) continue;
            if (path.EndsWith("_trunk.png")) continue;
            string plantName = SysPath.GetFileNameWithoutExtension(path);
            if (SplitSheet(path, plantName, written, force)) processed++;
            else                                             skipped++;
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[PlantSheetSplitter] Done — processed {processed}, skipped {skipped} up-to-date, wrote {written.Count} sprite(s).{(force ? " (force)" : "")}");
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
            // Baker companions — not sprite sheets meant for in-game rendering.
            // Splitting them would pollute Split/ with useless plant folders.
            if (path.EndsWith("_blobs.png")) continue;
            if (path.EndsWith("_trunk.png")) continue;
            string plantName = SysPath.GetFileNameWithoutExtension(path);
            SplitSheet(path, plantName, written, force: true);
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[PlantSheetSplitter] Done — wrote {written.Count} sprite(s).");
    }

    // ── core ─────────────────────────────────────────────────────────────────
    // sheetName is the file's base name (e.g. "tree" or "tree_sway"). For
    // `_sway` sheets we strip the suffix to find the plant folder and append
    // the suffix to each cell name so output lands at
    // `Split/{plant}/g{i}_sway.png` next to `g{i}.png` — that's where
    // SpriteNormalMapGenerator looks when wiring the `_SwayMask` secondary.
    //
    // Returns true if the sheet was processed, false if skipped (up-to-date).
    static bool SplitSheet(string sheetPath, string sheetName, List<string> written, bool force) {
        TextureImporter imp = AssetImporter.GetAtPath(sheetPath) as TextureImporter;
        if (imp == null) { Debug.LogWarning($"[PlantSheetSplitter] No importer for {sheetPath}"); return false; }

        string plantFolder = sheetName;
        string cellSuffix  = "";
        if (sheetName.EndsWith("_sway")) {
            plantFolder = sheetName.Substring(0, sheetName.Length - "_sway".Length);
            cellSuffix  = "_sway";
        }

        if (!force && IsUpToDate(sheetPath, plantFolder, imp)) return false;

        bool wasReadable = imp.isReadable;
        if (!wasReadable) { imp.isReadable = true; imp.SaveAndReimport(); }

        Texture2D sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(sheetPath);
        if (sheet == null) {
            if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
            Debug.LogWarning($"[PlantSheetSplitter] Could not load {sheetPath}");
            return false;
        }

        string outDir = SysPath.Combine(SplitFolder, plantFolder).Replace('\\', '/');
        SysDir.CreateDirectory(outDir);

        int numCols = sheet.width  / CellSize;
        int numRows = sheet.height / CellSize;
        if (numCols <= 0 || numRows <= 0) {
            Debug.LogWarning($"[PlantSheetSplitter] {sheetPath} is smaller than one cell ({CellSize}px) — skipping");
            if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
            return false;
        }
        if (numRows > 2) {
            Debug.LogWarning($"[PlantSheetSplitter] {sheetPath} has {numRows} rows — only rows 0 (b*) and 1 (g*) will be split.");
        }

        // Row 0 in the image (top) is row 0 here. Pixel-space y is bottom-up,
        // so row 0 lives at py = sheet.height - CellSize.
        //   1-row sheet  → row 0 → "g"  (legacy behaviour)
        //   2+-row sheet → row 0 → "b", row 1 → "g"
        for (int row = 0; row < Mathf.Min(numRows, 2); row++) {
            string prefix = (numRows == 1)
                ? "g"
                : (row == 0 ? "b" : "g");

            int py = sheet.height - (row + 1) * CellSize;
            if (py < 0) continue;

            for (int col = 0; col < numCols; col++) {
                string slotName = prefix + col + cellSuffix;
                int px = col * CellSize;

                Color[] pixels = sheet.GetPixels(px, py, CellSize, CellSize);

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
    //   - source PNG content (mtime proxy — sheet dim changes flow through this)
    //   - CellSize (so a future code change re-splits everything)
    //   - output plant folder existence (user may have deleted the whole folder)
    // Individual missing output files are NOT detected — force-regen to recover.
    static bool IsUpToDate(string sheetPath, string plantFolder, TextureImporter imp) {
        string stored = GetUserDataValue(imp, CacheKey);
        if (string.IsNullOrEmpty(stored)) return false;
        if (stored != ComputeCacheKey(sheetPath)) return false;
        string outDir = SysPath.Combine(SplitFolder, plantFolder).Replace('\\', '/');
        if (!SysDir.Exists(outDir)) return false;
        return true;
    }

    static string ComputeCacheKey(string sheetPath) {
        var sb = new StringBuilder();
        sb.Append(SysFile.GetLastWriteTimeUtc(sheetPath).Ticks);
        sb.Append('|').Append(CellSize);
        return Md5(sb.ToString());
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

    // ── userData helpers ─────────────────────────────────────────────────────
    // Importer userData carries semicolon-separated key=value pairs. Same
    // format SpriteNormalMapGenerator uses, so `plantSplitCacheKey` can coexist
    // with `normalsCacheKey` on the same sheet.
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
