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
// Output per cell (cellName = e.g. "g0" or "b4"):
//   {cellName}_static.png  — trunk + non-foliage layer; the bit that
//                            never moves. Plant.cs sets this as the tile
//                            SR's sprite.
//   {cellName}_b{i}.png    — one sprite per unique blob colour. Pixels read
//                            from the base sprite (so foliage keeps its
//                            painted colour); transparent everywhere outside
//                            the blob. Plant.cs spawns one child SR per file
//                            and PlantController.Update translates each
//                            frame by sin(t + φ) * amplitude * wind, pixel-
//                            snapped.
//
// Plus one sway_meta.json per plant listing per-blob phase + isStatic for
// every cell. Blob indices are sorted by colour-hash so the same colour gets
// the same slot across growth stages where it appears.
//
// Output settings: PPU=16, point filter, uncompressed, clamp wrap, Single
// sprite mode (one sprite per file).
//
// Source:  Assets/Resources/Sprites/Plants/Sheets/{plantName}.png
//          Assets/Resources/Sprites/Plants/Sheets/{plantName}_blobs.png
//          Assets/Resources/Sprites/Plants/Sheets/{plantName}_trunk.png   (optional)
// Output:  Assets/Resources/Sprites/Plants/Split/{plantName}/g{stage}_static.png
//          Assets/Resources/Sprites/Plants/Split/{plantName}/g{stage}_b{i}.png
//          Assets/Resources/Sprites/Plants/Split/{plantName}/b{stage}_static.png
//          Assets/Resources/Sprites/Plants/Split/{plantName}/b{stage}_b{i}.png
//          Assets/Resources/Sprites/Plants/Split/{plantName}/sway_meta.json
//
// Usage:
//   Tools → Bake All Plant Blob Sway        — process every *_blobs.png
//   Right-click *_blobs.png → Bake Plant Blob Sway
public static class PlantBlobBaker {
    const int    CellSize     = 16;
    const string BlobsSuffix  = "_blobs";
    const string TrunkSuffix  = "_trunk";
    const string SheetsFolder = "Assets/Resources/Sprites/Plants/Sheets";
    const string SplitFolder  = "Assets/Resources/Sprites/Plants/Split";
    const string MetaFileName = "sway_meta.json";

    // ── menu entries ─────────────────────────────────────────────────────────
    [MenuItem("Tools/Bake All Plant Blob Sway")]
    internal static void BakeAll() {
        var written  = new List<string>();
        int plantCnt = 0;
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { SheetsFolder });
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (!fn.EndsWith(BlobsSuffix)) continue;
            string plantName = fn.Substring(0, fn.Length - BlobsSuffix.Length);
            BakePlant(plantName, written);
            plantCnt++;
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[PlantBlobBaker] Processed {plantCnt} plant(s), wrote {written.Count} sprite(s).");
    }

    [MenuItem("Assets/Bake Plant Blob Sway", validate = true)]
    static bool ValidateSelected() {
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D)) continue;
            string path = AssetDatabase.GetAssetPath(o);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (path.StartsWith(SheetsFolder) && fn.EndsWith(BlobsSuffix)) return true;
        }
        return false;
    }

    [MenuItem("Assets/Bake Plant Blob Sway")]
    static void BakeSelected() {
        var written = new List<string>();
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D)) continue;
            string path = AssetDatabase.GetAssetPath(o);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (!path.StartsWith(SheetsFolder) || !fn.EndsWith(BlobsSuffix)) continue;
            string plantName = fn.Substring(0, fn.Length - BlobsSuffix.Length);
            BakePlant(plantName, written);
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[PlantBlobBaker] Wrote {written.Count} sprite(s).");
    }

    // Convenience combo — bake then generate normal maps for the new sprites.
    // Mirrors PlantSheetSplitter's `Split + Generate Normal Maps` pattern so
    // the typical artist workflow is one click. NormalMapGen.GenerateAll
    // walks Assets/Resources/Sprites/Plants and picks up the new `_static`
    // and `_b*` PNGs automatically (each blob's silhouette gets its own
    // bevelled normals; the static layer behaves like any other sprite).
    [MenuItem("Tools/Bake Plant Blob Sway + Generate Normal Maps")]
    static void BakeAllThenGenerateNormals() {
        BakeAll();
        SpriteNormalMapGenerator.GenerateAll();
    }

    // ── core ─────────────────────────────────────────────────────────────────
    static void BakePlant(string plantName, List<string> written) {
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

            // Wipe stale baker outputs (any prior layout — strip files, individual
            // frame files, individual blob files). Bounded regexes so we only
            // touch our own artefacts, never the splitter's g0.png/b0.png.
            CleanOldBakerOutputs(outDir);

            var allCells = new List<PlantSwayCellMeta>();

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

                    PlantSwayCellMeta cellMeta = BakeCell(prefix, col, basePixels, maskPixels, trunkPixels, outDir, written);
                    if (cellMeta != null) allCells.Add(cellMeta);
                }
            }

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

    // Bakes one 16x16 cell: emits a static-layer sprite, one sprite per blob
    // colour, and returns the cell's metadata. Blobs are sorted by colour-hash
    // (via SortedSet<uint>) so the index slot is stable across growth stages
    // where the same colour appears.
    static PlantSwayCellMeta BakeCell(
        string prefix, int col,
        Color[] basePx, Color[] maskPx, Color[] trunkPx,
        string outDir, List<string> written)
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
        string staticPath = SysPath.Combine(outDir, $"{cellName}_static.png").Replace('\\', '/');
        WritePng(staticPath, staticLayer);
        written.Add(staticPath);

        // One sprite per blob — opaque only where this blob's pixels live.
        var blobMetas = new List<PlantSwayBlobMeta>();
        int blobIdx = 0;
        foreach (uint color in blobColors) {
            Color[] blobLayer = new Color[CellSize * CellSize];  // default Color.clear
            for (int i = 0; i < CellSize * CellSize; i++) {
                if (maskPx[i].a <= 0f) continue;
                if (PackRGB(maskPx[i]) != color) continue;
                blobLayer[i] = basePx[i];
            }

            string blobPath = SysPath.Combine(outDir, $"{cellName}_b{blobIdx}.png").Replace('\\', '/');
            WritePng(blobPath, blobLayer);
            written.Add(blobPath);

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

    // Sweeps every prior baker output (frame strips, individual frames,
    // individual blob/static sprites, and their `_n.png` normal-map siblings)
    // from the output folder. Bounded to strict regexes so we only ever
    // touch our own artefacts — splitter outputs (g0.png, b0.png,
    // g0_sway.png, etc.) and their normal maps are untouched.
    //
    // The optional `(_n)?` group in each regex catches the normal-map
    // companion that NormalMapGen wrote next to each baker output —
    // important when re-baking with fewer blobs (`g0_b7_n.png` would
    // otherwise persist as an orphan after `g0_b7.png` is gone).
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

    static void WritePng(string path, Color[] pixels) {
        Texture2D t = new Texture2D(CellSize, CellSize, TextureFormat.RGBA32, false);
        t.SetPixels(pixels);
        t.Apply();
        SysFile.WriteAllBytes(path, t.EncodeToPNG());
        Object.DestroyImmediate(t);
    }

    static void ApplyImportSettings(List<string> paths) {
        foreach (string path in paths) {
            AssetDatabase.ImportAsset(path);
            TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) continue;
            imp.textureType         = TextureImporterType.Sprite;
            imp.spriteImportMode    = SpriteImportMode.Single;
            imp.spritePixelsPerUnit = 16;
            imp.filterMode          = FilterMode.Point;
            imp.textureCompression  = TextureImporterCompression.Uncompressed;
            imp.wrapMode            = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }
    }
}
