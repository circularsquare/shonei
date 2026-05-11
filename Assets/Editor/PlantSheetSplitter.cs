using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
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
            // Baker companions — not sprite sheets meant for in-game rendering.
            // Splitting them would pollute Split/ with useless plant folders.
            if (path.EndsWith("_blobs.png")) continue;
            if (path.EndsWith("_trunk.png")) continue;
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
            // Baker companions — not sprite sheets meant for in-game rendering.
            // Splitting them would pollute Split/ with useless plant folders.
            if (path.EndsWith("_blobs.png")) continue;
            if (path.EndsWith("_trunk.png")) continue;
            string plantName = SysPath.GetFileNameWithoutExtension(path);
            SplitSheet(path, plantName, written);
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
    static void SplitSheet(string sheetPath, string sheetName, List<string> written) {
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

        string plantFolder = sheetName;
        string cellSuffix  = "";
        if (sheetName.EndsWith("_sway")) {
            plantFolder = sheetName.Substring(0, sheetName.Length - "_sway".Length);
            cellSuffix  = "_sway";
        }

        string outDir = SysPath.Combine(SplitFolder, plantFolder).Replace('\\', '/');
        SysDir.CreateDirectory(outDir);

        int numCols = sheet.width  / CellSize;
        int numRows = sheet.height / CellSize;
        if (numCols <= 0 || numRows <= 0) {
            Debug.LogWarning($"[PlantSheetSplitter] {sheetPath} is smaller than one cell ({CellSize}px) — skipping");
            if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
            return;
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
