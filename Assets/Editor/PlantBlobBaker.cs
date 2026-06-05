using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;
using SysDir  = System.IO.Directory;

// Bakes per-blob sprites + sway metadata for plants that ship a
// `{plantName}_blobs.png` companion sheet next to the base `{plantName}.png`.
//
// Mask format: each unique non-transparent colour in the mask marks one blob
// (a foliage cluster that moves as a unit). Transparent mask pixels (= trunk
// and non-foliage) stay still. 16-px cell grid, optional 2-row b*/g* layout
// — same convention as PlantSheetSplitter.
//
// Optional trunk companion: `{plantName}_trunk.png` — same cell layout, holds
// the persistent trunk silhouette that lives BEHIND the foliage. When a blob
// shifts off its rest position at runtime, the pixels it vacates fall back to
// the trunk sheet (trunk shows through the gap). Without this companion, the
// gap shows transparency (sky).
//
// Static blobs: pure black (0,0,0) and pure white (255,255,255) are still
// recognised as blob colours so they cover the trunk underneath, but they
// carry isStatic=true in the metadata — runtime never moves them.
//
// Output per plant (ONE Multiple-mode sprite sheet, not loose per-cell files):
//   {plantName}_blobs_baked.png — every cell's static layer + per-blob sprite
//       packed into one texture, sliced into named 16x16 sub-sprites:
//         {cellName}_static  — trunk + non-foliage layer; the bit that never
//                              moves. Plant.cs sets this as the tile SR's sprite.
//         {cellName}_b{i}    — one sub-sprite per unique blob colour. Pixels
//                              read from the base sprite (so foliage keeps its
//                              painted colour); transparent everywhere outside
//                              the blob. Plant.cs spawns one child SR per blob
//                              and PlantController.Update translates each frame
//                              by sin(t + φ) * amplitude * wind, pixel-snapped.
//       Loaded at runtime via PlantBlobSpriteCache (Resources.LoadAll once per
//       plant). Sub-sprites all use Center pivot + PPU=16 so a child blob SR at
//       localPosition zero lands exactly on its rest cell.
//   sway_meta.json — per-blob phase + isStatic for every cell (PlantSwayMetaCache).
//
// One sheet + one importer config per plant keeps the asset count (and git
// churn) tiny — the previous layout emitted ~100+ loose 16x16 PNGs per plant.
// SpriteNormalMapGenerator processes the sheet per-slice automatically (each
// blob gets its own bevelled normals) into one shared {..}_blobs_baked_n.png.
//
// Output settings: PPU=16, point filter, uncompressed, clamp wrap, no mipmaps,
// Multiple sprite mode (one named sub-sprite per layer).
//
// Source:  Assets/Resources/Sprites/Plants/Sheets/{plantName}.png
//          Assets/Resources/Sprites/Plants/Sheets/{plantName}_blobs.png
//          Assets/Resources/Sprites/Plants/Sheets/{plantName}_trunk.png   (optional)
// Output:  Assets/Resources/Sprites/Plants/Split/{plantName}/{plantName}_blobs_baked.png
//          Assets/Resources/Sprites/Plants/Split/{plantName}/sway_meta.json
//
// Usage:
//   Tools → Split Plant Blob Sheets + Normals  — process every *_blobs.png + normals
//   Right-click *_blobs.png → Split Plant Blob Sheet
public static class PlantBlobBaker {
    const int    CellSize     = 16;
    const string BlobsSuffix  = "_blobs";
    const string TrunkSuffix  = "_trunk";
    const string SheetSuffix  = "_blobs_baked";   // packed output sheet
    const string SheetsFolder = "Assets/Resources/Sprites/Plants/Sheets";
    const string SplitFolder  = "Assets/Resources/Sprites/Plants/Split";
    const string MetaFileName = "sway_meta.json";

    // One 16x16 layer destined for a slot in the packed sheet.
    struct SheetLayer {
        public string  name;    // slice/sub-sprite name, e.g. "g0_static" or "b4_b2"
        public Color[] pixels;  // CellSize*CellSize, row-major
    }

    // Slice metadata to apply after the sheet PNGs are imported. Keyed by the
    // sheet's asset path; consumed (and cleared) by ApplySheetImportSettings.
    static readonly Dictionary<string, SpriteMetaData[]> sheetMetas
        = new Dictionary<string, SpriteMetaData[]>();

    // ── menu entries ─────────────────────────────────────────────────────────
    // (no standalone menu item — invoked by the "Split Plant Blob Sheets +
    //  Normals" combo below; the split-only entry was removed.)
    internal static void BakeAll() {
        var sheets   = new List<string>();
        int plantCnt = 0;
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { SheetsFolder });
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (!fn.EndsWith(BlobsSuffix)) continue;
            string plantName = fn.Substring(0, fn.Length - BlobsSuffix.Length);
            BakePlant(plantName, sheets);
            plantCnt++;
        }
        AssetDatabase.Refresh();
        ApplySheetImportSettings(sheets);
        Debug.Log($"[PlantBlobBaker] Processed {plantCnt} plant(s), wrote {sheets.Count} sheet(s).");
    }

    [MenuItem("Assets/Split Plant Blob Sheet", validate = true)]
    static bool ValidateSelected() {
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D)) continue;
            string path = AssetDatabase.GetAssetPath(o);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (path.StartsWith(SheetsFolder) && fn.EndsWith(BlobsSuffix)) return true;
        }
        return false;
    }

    [MenuItem("Assets/Split Plant Blob Sheet")]
    static void BakeSelected() {
        var sheets = new List<string>();
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D)) continue;
            string path = AssetDatabase.GetAssetPath(o);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (!path.StartsWith(SheetsFolder) || !fn.EndsWith(BlobsSuffix)) continue;
            string plantName = fn.Substring(0, fn.Length - BlobsSuffix.Length);
            BakePlant(plantName, sheets);
        }
        AssetDatabase.Refresh();
        ApplySheetImportSettings(sheets);
        Debug.Log($"[PlantBlobBaker] Wrote {sheets.Count} sheet(s).");
    }

    // Convenience combo — bake then generate normal maps for the new sheets.
    // Mirrors PlantSheetSplitter's `Split + Generate Normal Maps` pattern so
    // the typical artist workflow is one click. NormalMapGen.GenerateAll walks
    // Assets/Resources/Sprites/Plants and picks up each `_blobs_baked` sheet
    // automatically — Multiple-mode sheets are processed per-slice, so every
    // blob gets its own bevelled normals in one shared `_n` sheet.
    [MenuItem("Tools/Split Plant Blob Sheets + Normals", priority = 102)]
    static void BakeAllThenGenerateNormals() {
        BakeAll();
        SpriteNormalMapGenerator.GenerateAll();
    }

    // ── core ─────────────────────────────────────────────────────────────────
    static void BakePlant(string plantName, List<string> sheets) {
        string basePath  = SysPath.Combine(SheetsFolder, plantName + ".png").Replace('\\', '/');
        string maskPath  = SysPath.Combine(SheetsFolder, plantName + BlobsSuffix + ".png").Replace('\\', '/');
        string trunkPath = SysPath.Combine(SheetsFolder, plantName + TrunkSuffix + ".png").Replace('\\', '/');

        if (!SysFile.Exists(basePath)) {
            Debug.LogWarning($"[PlantBlobBaker] Missing base sheet {basePath} — skipping {plantName}");
            return;
        }
        if (!SysFile.Exists(maskPath)) return;
        bool hasTrunk = SysFile.Exists(trunkPath);

        TextureImporter baseImp  = AssetImporter.GetAtPath(basePath)  as TextureImporter;
        TextureImporter maskImp  = AssetImporter.GetAtPath(maskPath)  as TextureImporter;
        TextureImporter trunkImp = hasTrunk ? AssetImporter.GetAtPath(trunkPath) as TextureImporter : null;
        if (baseImp == null || maskImp == null || (hasTrunk && trunkImp == null)) {
            Debug.LogWarning($"[PlantBlobBaker] Missing importer for {plantName}");
            return;
        }

        // Temporarily flip isReadable for sheets that aren't normally CPU-readable.
        // Restored in finally so the project state goes back to baseline.
        bool baseRW  = baseImp.isReadable;
        bool maskRW  = maskImp.isReadable;
        bool trunkRW = hasTrunk && trunkImp.isReadable;
        if (!baseRW)              { baseImp.isReadable  = true; baseImp.SaveAndReimport();  }
        if (!maskRW)              { maskImp.isReadable  = true; maskImp.SaveAndReimport();  }
        if (hasTrunk && !trunkRW) { trunkImp.isReadable = true; trunkImp.SaveAndReimport(); }

        try {
            Texture2D baseTex  = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath);
            Texture2D maskTex  = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
            Texture2D trunkTex = hasTrunk ? AssetDatabase.LoadAssetAtPath<Texture2D>(trunkPath) : null;
            if (baseTex == null || maskTex == null || (hasTrunk && trunkTex == null)) {
                Debug.LogWarning($"[PlantBlobBaker] Could not load textures for {plantName}");
                return;
            }
            if (baseTex.width != maskTex.width || baseTex.height != maskTex.height) {
                Debug.LogError($"[PlantBlobBaker] {plantName}: base {baseTex.width}x{baseTex.height} vs mask {maskTex.width}x{maskTex.height} — must match");
                return;
            }
            if (hasTrunk && (trunkTex.width != baseTex.width || trunkTex.height != baseTex.height)) {
                Debug.LogError($"[PlantBlobBaker] {plantName}: trunk {trunkTex.width}x{trunkTex.height} doesn't match base {baseTex.width}x{baseTex.height}");
                return;
            }

            int numCols = baseTex.width  / CellSize;
            int numRows = baseTex.height / CellSize;
            if (numCols <= 0 || numRows <= 0) {
                Debug.LogWarning($"[PlantBlobBaker] {basePath} smaller than one cell ({CellSize}px) — skipping");
                return;
            }
            if (numRows > 2) {
                Debug.LogWarning($"[PlantBlobBaker] {basePath} has {numRows} rows — only rows 0 (b*) and 1 (g*) baked.");
            }

            string outDir = SysPath.Combine(SplitFolder, plantName).Replace('\\', '/');
            SysDir.CreateDirectory(outDir);

            // Wipe stale per-cell baker outputs from the OLD loose-file layout
            // (strip files, individual frame files, individual blob files) so a
            // re-bake migrates cleanly. Bounded regexes so we only touch our own
            // artefacts, never the splitter's g0.png/b0.png or the new sheet.
            CleanOldBakerOutputs(outDir);

            var allCells = new List<PlantSwayCellMeta>();
            var layers   = new List<SheetLayer>();   // every static + blob sub-sprite, in emit order

            for (int row = 0; row < Mathf.Min(numRows, 2); row++) {
                string prefix = (numRows == 1) ? "g" : (row == 0 ? "b" : "g");
                int py = baseTex.height - (row + 1) * CellSize;
                if (py < 0) continue;

                for (int col = 0; col < numCols; col++) {
                    int px = col * CellSize;
                    Color[] basePixels  = baseTex.GetPixels(px, py, CellSize, CellSize);
                    Color[] maskPixels  = maskTex.GetPixels(px, py, CellSize, CellSize);
                    Color[] trunkPixels = hasTrunk ? trunkTex.GetPixels(px, py, CellSize, CellSize) : null;

                    bool hasContent = false;
                    foreach (var p in basePixels) { if (p.a > 0f) { hasContent = true; break; } }
                    if (!hasContent) continue;

                    PlantSwayCellMeta cellMeta = BakeCell(prefix, col, basePixels, maskPixels, trunkPixels, layers);
                    if (cellMeta != null) allCells.Add(cellMeta);
                }
            }

            // Pack every layer into one Multiple-mode sheet (slice metadata is
            // applied later, after the PNG is imported).
            string sheetPath = SysPath.Combine(outDir, plantName + SheetSuffix + ".png").Replace('\\', '/');
            WriteBlobSheet(sheetPath, layers, sheets);

            // Write the JSON sidecar describing every cell's per-blob phase data.
            // Loaded once at runtime via PlantSwayMetaCache.
            var meta = new PlantSwayMeta { cells = allCells.ToArray() };
            string metaPath = SysPath.Combine(outDir, MetaFileName).Replace('\\', '/');
            SysFile.WriteAllText(metaPath, JsonUtility.ToJson(meta, prettyPrint: true));
        } finally {
            if (!baseRW)              { baseImp.isReadable  = false; baseImp.SaveAndReimport();  }
            if (!maskRW)              { maskImp.isReadable  = false; maskImp.SaveAndReimport();  }
            if (hasTrunk && !trunkRW) { trunkImp.isReadable = false; trunkImp.SaveAndReimport(); }
        }
    }

    // Bakes one 16x16 cell: appends a static-layer sub-sprite plus one sub-sprite
    // per blob colour to `layers`, and returns the cell's metadata. Blobs are
    // sorted by colour-hash (via SortedSet<uint>) so the index slot is stable
    // across growth stages where the same colour appears.
    static PlantSwayCellMeta BakeCell(
        string prefix, int col,
        Color[] basePx, Color[] maskPx, Color[] trunkPx,
        List<SheetLayer> layers)
    {
        string cellName = $"{prefix}{col}";

        // Collect blob colours in deterministic (sort-by-key) order.
        var blobColors = new SortedSet<uint>();
        for (int i = 0; i < maskPx.Length; i++) {
            if (maskPx[i].a <= 0f) continue;
            blobColors.Add(PackRGB(maskPx[i]));
        }

        // Static layer:
        //   - mask transparent → base pixel (visible trunk, ground, etc.)
        //   - mask colored     → trunk pixel from the trunk companion, or
        //                         transparent if no companion. This is what
        //                         a shifted blob "reveals" at runtime.
        Color[] staticLayer = new Color[CellSize * CellSize];
        for (int i = 0; i < CellSize * CellSize; i++) {
            if (maskPx[i].a <= 0f) staticLayer[i] = basePx[i];
            else                   staticLayer[i] = (trunkPx != null) ? trunkPx[i] : Color.clear;
        }
        layers.Add(new SheetLayer { name = $"{cellName}_static", pixels = staticLayer });

        // One sub-sprite per blob — opaque only where this blob's pixels live.
        var blobMetas = new List<PlantSwayBlobMeta>();
        int blobIdx = 0;
        foreach (uint color in blobColors) {
            Color[] blobLayer = new Color[CellSize * CellSize];  // default Color.clear
            for (int i = 0; i < CellSize * CellSize; i++) {
                if (maskPx[i].a <= 0f) continue;
                if (PackRGB(maskPx[i]) != color) continue;
                blobLayer[i] = basePx[i];
            }

            layers.Add(new SheetLayer { name = $"{cellName}_b{blobIdx}", pixels = blobLayer });

            blobMetas.Add(new PlantSwayBlobMeta {
                phase    = PhaseFromColor(color),
                isStatic = IsStaticBlob(color),
            });
            blobIdx++;
        }

        return new PlantSwayCellMeta {
            cellName = cellName,
            blobs    = blobMetas.ToArray(),
        };
    }

    // Composites every layer into one square-ish sheet (16x16 cells, row-major,
    // top-left first), writes the PNG, and records the slice metadata for
    // ApplySheetImportSettings to apply once the asset is imported. Sub-sprite
    // rects are full 16x16 with Center pivot so runtime positioning matches the
    // old one-file-per-blob layout exactly.
    static void WriteBlobSheet(string path, List<SheetLayer> layers, List<string> sheets) {
        if (layers.Count == 0) {
            // No content (e.g. an all-transparent _blobs sheet) — remove any
            // stale sheet so we don't leave an orphan behind.
            if (SysFile.Exists(path)) {
                SysFile.Delete(path);
                string m = path + ".meta";
                if (SysFile.Exists(m)) SysFile.Delete(m);
            }
            return;
        }

        int cols = Mathf.CeilToInt(Mathf.Sqrt(layers.Count));
        int rows = Mathf.CeilToInt(layers.Count / (float)cols);
        int W = cols * CellSize, H = rows * CellSize;

        Color[] sheet = new Color[W * H];   // default Color.clear
        var metas = new SpriteMetaData[layers.Count];

        for (int k = 0; k < layers.Count; k++) {
            int col = k % cols;
            int rowFromTop = k / cols;
            int px = col * CellSize;
            int py = H - (rowFromTop + 1) * CellSize;   // texture origin is bottom-left

            Color[] src = layers[k].pixels;
            for (int yy = 0; yy < CellSize; yy++)
                for (int xx = 0; xx < CellSize; xx++)
                    sheet[(py + yy) * W + (px + xx)] = src[yy * CellSize + xx];

            metas[k] = new SpriteMetaData {
                name      = layers[k].name,
                rect      = new Rect(px, py, CellSize, CellSize),
                alignment = (int)SpriteAlignment.Center,
                pivot     = new Vector2(0.5f, 0.5f),
            };
        }

        Texture2D tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
        tex.SetPixels(sheet);
        tex.Apply();
        SysFile.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        sheetMetas[path] = metas;
        sheets.Add(path);
    }

    // Sweeps every prior LOOSE baker output (frame strips, individual frames,
    // individual blob/static sprites, and their `_n.png` normal-map siblings)
    // from the output folder. Bounded to strict regexes so we only ever touch
    // our own artefacts — splitter outputs (g0.png, b0.png, g0_sway.png, etc.),
    // their normal maps, and the new packed `_blobs_baked` sheet are untouched.
    //
    // The optional `(_n)?` group in each regex catches the normal-map companion
    // that NormalMapGen wrote next to each old loose output.
    static void CleanOldBakerOutputs(string outDir) {
        if (!SysDir.Exists(outDir)) return;
        var rxStatic = new System.Text.RegularExpressions.Regex(@"^[bg]\d+_static(_n)?$");
        var rxBlob   = new System.Text.RegularExpressions.Regex(@"^[bg]\d+_b\d+(_n)?$");
        var rxAnim   = new System.Text.RegularExpressions.Regex(@"^[bg]\d+_anim(_n)?$");
        var rxFrame  = new System.Text.RegularExpressions.Regex(@"^[bg]\d+_f\d+(_n)?$");
        foreach (string file in SysDir.GetFiles(outDir, "*.png")) {
            string stem = SysPath.GetFileNameWithoutExtension(file);
            if (!rxStatic.IsMatch(stem) && !rxBlob.IsMatch(stem)
                && !rxAnim.IsMatch(stem) && !rxFrame.IsMatch(stem)) continue;
            SysFile.Delete(file);
            string meta = file + ".meta";
            if (SysFile.Exists(meta)) SysFile.Delete(meta);
        }
    }

    // ── utility ──────────────────────────────────────────────────────────────
    static uint PackRGB(Color c) {
        uint r = (uint)Mathf.RoundToInt(c.r * 255f);
        uint g = (uint)Mathf.RoundToInt(c.g * 255f);
        uint b = (uint)Mathf.RoundToInt(c.b * 255f);
        return (r << 16) | (g << 8) | b;
    }

    // Knuth multiplicative hash → [0, 2π). Same colour always maps to the
    // same phase so a "green blob" rustles consistently across growth stages.
    static float PhaseFromColor(uint rgb) {
        unchecked {
            uint h = rgb * 2654435761u;
            return (h & 0xFFFFFFu) / (float)0xFFFFFF * 2f * Mathf.PI;
        }
    }

    // Pure black or pure white in the mask → blob is recognised but pinned.
    static bool IsStaticBlob(uint rgb) {
        return rgb == 0x000000u || rgb == 0xFFFFFFu;
    }

    // Imports every freshly-written sheet as a Multiple-mode sprite with its
    // named slices. Wrapped in StartAssetEditing so all reimports coalesce into
    // one final sweep instead of N synchronous round-trips (cf. the same pattern
    // in SpriteNormalMapGenerator's batch path).
    static void ApplySheetImportSettings(List<string> paths) {
        try {
            AssetDatabase.StartAssetEditing();
            foreach (string path in paths) {
                AssetDatabase.ImportAsset(path);
                TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                imp.textureType         = TextureImporterType.Sprite;
                imp.spriteImportMode    = SpriteImportMode.Multiple;
                imp.spritePixelsPerUnit = CellSize;
                imp.filterMode          = FilterMode.Point;
                imp.textureCompression  = TextureImporterCompression.Uncompressed;
                imp.wrapMode            = TextureWrapMode.Clamp;
                imp.mipmapEnabled       = false;
                if (sheetMetas.TryGetValue(path, out var metas)) imp.spritesheet = metas;
                imp.SaveAndReimport();
            }
        } finally {
            AssetDatabase.StopAssetEditing();
            sheetMetas.Clear();
        }
    }
}
