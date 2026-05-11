using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;
using SysDir  = System.IO.Directory;

// Bakes per-blob wind-sway animation frames for plants that ship a
// `{plantName}_blobs.png` companion sheet next to the base `{plantName}.png`.
//
// Mask format: each unique non-transparent color in the mask marks one blob
// (a foliage cluster that moves as a unit). Transparent mask pixels (= trunk
// and non-foliage) stay still. The mask sheet uses the same cell grid as the
// base sheet (16 px cells, optional 2-row b*/g* layout — see PlantSheetSplitter).
//
// Static blobs: pure black (0,0,0) and pure white (255,255,255) are treated as
// blob colors but pinned to offset 0 — they're "foliage that doesn't sway",
// useful for anchor leaves at the trunk or other regions where movement would
// read as unnatural. They still cover the trunk layer (no peek-through gap),
// just don't shift.
//
// Optional trunk companion: `{plantName}_trunk.png` — same cell layout, holds
// the persistent trunk silhouette that lives BEHIND the foliage. When a blob
// shifts off its rest position, the pixels it vacates fall back to the trunk
// sheet (trunk shows through the gap). Without this companion, vacated blob
// pixels fall back to transparency (sky shows through) — fine for outermost
// foliage but creates "holes" where leaves overlap the trunk in the base art.
//
// Algorithm (per cell, X-only sway):
//   1. Collect unique blob colors in the cell's mask. Hash each color → φ ∈ [0, 2π).
//   2. Build the static layer:
//        - mask transparent → base pixel (visible trunk, ground, etc.)
//        - mask colored     → trunk pixel if a trunk sheet exists, else clear
//   3. For frame f ∈ [0, NumFrames): per blob, dx = sin(2π·f/N + φ) > 0 ? 1 : 0.
//      Paint blob pixels at (x + dx, y), reading colors from the base.
//   4. Emit `{prefix}{col}_f{frame}.png` (prefix = b for top row, g for bottom).
//
// NumFrames at 6 fps gives a 4-second loop. dx ∈ {0, +1} only — gentle 1-pixel
// flutter that reads as "leaves catching the breeze" without making rigid
// trunks visibly slide. Each blob rests for roughly half the cycle.
//
// Output settings mirror PlantSheetSplitter: PPU=16, point filter, uncompressed,
// clamp wrap.
//
// Source:  Assets/Resources/Sprites/Plants/Sheets/{plantName}.png
//          Assets/Resources/Sprites/Plants/Sheets/{plantName}_blobs.png
//          Assets/Resources/Sprites/Plants/Sheets/{plantName}_trunk.png   (optional)
// Output:  Assets/Resources/Sprites/Plants/Split/{plantName}/g{stage}_f{frame}.png
//          Assets/Resources/Sprites/Plants/Split/{plantName}/b{stage}_f{frame}.png
//
// Usage:
//   Tools → Bake All Plant Blob Frames               — process every *_blobs.png
//   Right-click a *_blobs.png → Bake Plant Blob Frames
public static class PlantBlobFrameBaker {
    const int    CellSize     = 16;
    const int    NumFrames    = 24;            // 4 s loop at 6 fps
    const string BlobsSuffix  = "_blobs";
    const string TrunkSuffix  = "_trunk";
    const string SheetsFolder = "Assets/Resources/Sprites/Plants/Sheets";
    const string SplitFolder  = "Assets/Resources/Sprites/Plants/Split";

    // ── menu entries ─────────────────────────────────────────────────────────
    [MenuItem("Tools/Bake All Plant Blob Frames")]
    internal static void BakeAll() {
        var written  = new List<string>();
        int plantCnt = 0;

        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { SheetsFolder });
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (!fn.EndsWith(BlobsSuffix)) continue;
            string plantName = fn.Substring(0, fn.Length - BlobsSuffix.Length);
            BakeSheet(plantName, written);
            plantCnt++;
        }

        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[PlantBlobFrameBaker] Done — processed {plantCnt} plant(s), wrote {written.Count} frame file(s).");
    }

    [MenuItem("Assets/Bake Plant Blob Frames", validate = true)]
    static bool ValidateSelected() {
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D)) continue;
            string path = AssetDatabase.GetAssetPath(o);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (path.StartsWith(SheetsFolder) && fn.EndsWith(BlobsSuffix)) return true;
        }
        return false;
    }

    [MenuItem("Assets/Bake Plant Blob Frames")]
    static void BakeSelected() {
        var written = new List<string>();
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D)) continue;
            string path = AssetDatabase.GetAssetPath(o);
            string fn   = SysPath.GetFileNameWithoutExtension(path);
            if (!path.StartsWith(SheetsFolder) || !fn.EndsWith(BlobsSuffix)) continue;
            string plantName = fn.Substring(0, fn.Length - BlobsSuffix.Length);
            BakeSheet(plantName, written);
        }
        AssetDatabase.Refresh();
        ApplyImportSettings(written);
        Debug.Log($"[PlantBlobFrameBaker] Done — wrote {written.Count} frame file(s).");
    }

    // ── core ─────────────────────────────────────────────────────────────────
    static void BakeSheet(string plantName, List<string> written) {
        string basePath  = SysPath.Combine(SheetsFolder, plantName + ".png").Replace('\\', '/');
        string maskPath  = SysPath.Combine(SheetsFolder, plantName + BlobsSuffix + ".png").Replace('\\', '/');
        string trunkPath = SysPath.Combine(SheetsFolder, plantName + TrunkSuffix + ".png").Replace('\\', '/');

        if (!SysFile.Exists(basePath)) {
            Debug.LogWarning($"[PlantBlobFrameBaker] Missing base sheet {basePath} — skipping {plantName}");
            return;
        }
        if (!SysFile.Exists(maskPath)) return;
        bool hasTrunk = SysFile.Exists(trunkPath);

        TextureImporter baseImp  = AssetImporter.GetAtPath(basePath)  as TextureImporter;
        TextureImporter maskImp  = AssetImporter.GetAtPath(maskPath)  as TextureImporter;
        TextureImporter trunkImp = hasTrunk ? AssetImporter.GetAtPath(trunkPath) as TextureImporter : null;
        if (baseImp == null || maskImp == null || (hasTrunk && trunkImp == null)) {
            Debug.LogWarning($"[PlantBlobFrameBaker] Missing importer for {plantName}");
            return;
        }

        // Temporarily flip isReadable for sheets that aren't normally CPU-readable,
        // matching PlantSheetSplitter's pattern. Restored in finally so the project
        // state goes back to baseline regardless of bake outcome.
        bool baseWasReadable  = baseImp.isReadable;
        bool maskWasReadable  = maskImp.isReadable;
        bool trunkWasReadable = hasTrunk && trunkImp.isReadable;
        if (!baseWasReadable)  { baseImp.isReadable  = true; baseImp.SaveAndReimport();  }
        if (!maskWasReadable)  { maskImp.isReadable  = true; maskImp.SaveAndReimport();  }
        if (hasTrunk && !trunkWasReadable) { trunkImp.isReadable = true; trunkImp.SaveAndReimport(); }

        try {
            Texture2D baseTex  = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath);
            Texture2D maskTex  = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
            Texture2D trunkTex = hasTrunk ? AssetDatabase.LoadAssetAtPath<Texture2D>(trunkPath) : null;
            if (baseTex == null || maskTex == null || (hasTrunk && trunkTex == null)) {
                Debug.LogWarning($"[PlantBlobFrameBaker] Could not load textures for {plantName}");
                return;
            }
            if (baseTex.width != maskTex.width || baseTex.height != maskTex.height) {
                Debug.LogError($"[PlantBlobFrameBaker] {plantName}: base sheet {baseTex.width}x{baseTex.height} vs mask {maskTex.width}x{maskTex.height} — must match");
                return;
            }
            if (hasTrunk && (trunkTex.width != baseTex.width || trunkTex.height != baseTex.height)) {
                Debug.LogError($"[PlantBlobFrameBaker] {plantName}: trunk sheet {trunkTex.width}x{trunkTex.height} doesn't match base {baseTex.width}x{baseTex.height} — must match");
                return;
            }

            int numCols = baseTex.width  / CellSize;
            int numRows = baseTex.height / CellSize;
            if (numCols <= 0 || numRows <= 0) {
                Debug.LogWarning($"[PlantBlobFrameBaker] {basePath} smaller than one cell ({CellSize}px) — skipping");
                return;
            }
            if (numRows > 2) {
                Debug.LogWarning($"[PlantBlobFrameBaker] {basePath} has {numRows} rows — only rows 0 (b*) and 1 (g*) baked.");
            }

            string outDir = SysPath.Combine(SplitFolder, plantName).Replace('\\', '/');
            SysDir.CreateDirectory(outDir);

            // Row 0 = b* (anchor / bottom-tile), row 1 = g* (upper tile). 1-row
            // sheets emit g* only. Matches PlantSheetSplitter exactly so the
            // Plant.cs sprite-resolution logic doesn't need a second convention.
            for (int row = 0; row < Mathf.Min(numRows, 2); row++) {
                string prefix = (numRows == 1) ? "g" : (row == 0 ? "b" : "g");
                int py = baseTex.height - (row + 1) * CellSize;
                if (py < 0) continue;

                for (int col = 0; col < numCols; col++) {
                    int px = col * CellSize;
                    Color[] basePixels  = baseTex.GetPixels(px, py, CellSize, CellSize);
                    Color[] maskPixels  = maskTex.GetPixels(px, py, CellSize, CellSize);
                    Color[] trunkPixels = hasTrunk ? trunkTex.GetPixels(px, py, CellSize, CellSize) : null;

                    // Skip cells with no base content. Cells with content but no
                    // mask coverage still get baked — they'll emit N identical
                    // frames (all blobs absent = no animation), which is harmless.
                    bool hasContent = false;
                    foreach (var p in basePixels) { if (p.a > 0f) { hasContent = true; break; } }
                    if (!hasContent) continue;

                    BakeCell(prefix, col, basePixels, maskPixels, trunkPixels, outDir, written);
                }
            }
        } finally {
            if (!baseWasReadable)  { baseImp.isReadable  = false; baseImp.SaveAndReimport();  }
            if (!maskWasReadable)  { maskImp.isReadable  = false; maskImp.SaveAndReimport();  }
            if (hasTrunk && !trunkWasReadable) { trunkImp.isReadable = false; trunkImp.SaveAndReimport(); }
        }
    }

    static void BakeCell(string prefix, int col, Color[] basePx, Color[] maskPx, Color[] trunkPx, string outDir, List<string> written) {
        // Pass 1: collect unique blob colors and assign each a deterministic phase.
        var phaseByBlob = new Dictionary<uint, float>();
        for (int i = 0; i < maskPx.Length; i++) {
            Color m = maskPx[i];
            if (m.a <= 0f) continue;
            uint key = PackRGB(m);
            if (!phaseByBlob.ContainsKey(key)) phaseByBlob[key] = PhaseFromColor(key);
        }

        // Static layer:
        //   - mask transparent → base pixel (visible trunk, ground, anything
        //     the artist didn't mark as a blob).
        //   - mask colored     → trunk pixel from the trunk companion if one
        //     was supplied, else transparent. This is what a shifted blob
        //     "reveals" — trunk peeks through gaps where leaves drifted aside.
        Color[] staticLayer = new Color[CellSize * CellSize];
        for (int i = 0; i < CellSize * CellSize; i++) {
            if (maskPx[i].a <= 0f) staticLayer[i] = basePx[i];
            else                   staticLayer[i] = (trunkPx != null) ? trunkPx[i] : Color.clear;
        }

        for (int f = 0; f < NumFrames; f++) {
            Color[] frame = new Color[CellSize * CellSize];
            System.Array.Copy(staticLayer, frame, frame.Length);

            float tPhase = 2f * Mathf.PI * f / NumFrames;

            // Composite order = dictionary iteration order = color-hash order.
            // Deterministic and good enough for v1 — overlap is 1 px at blob
            // boundaries and either blob's color reads fine as foliage.
            foreach (var kv in phaseByBlob) {
                uint  blobKey = kv.Key;
                float phi     = kv.Value;
                int   dx      = IsStaticBlob(blobKey)
                                ? 0
                                : (Mathf.Sin(tPhase + phi) > 0f ? 1 : 0);

                for (int y = 0; y < CellSize; y++) {
                    for (int x = 0; x < CellSize; x++) {
                        int idx = y * CellSize + x;
                        if (maskPx[idx].a <= 0f) continue;
                        if (PackRGB(maskPx[idx]) != blobKey) continue;

                        int tx = x + dx;
                        if (tx < 0 || tx >= CellSize) continue;  // clip at cell edge
                        frame[y * CellSize + tx] = basePx[idx];
                    }
                }
            }

            string fileName = $"{prefix}{col}_f{f}.png";
            string outPath  = SysPath.Combine(outDir, fileName).Replace('\\', '/');
            WritePng(outPath, frame);
            written.Add(outPath);
        }
    }

    // ── utility ──────────────────────────────────────────────────────────────
    // Pack RGB into a uint key for dictionary lookup. Alpha is ignored (we
    // already filtered mask.a > 0 upstream).
    static uint PackRGB(Color c) {
        uint r = (uint)Mathf.RoundToInt(c.r * 255f);
        uint g = (uint)Mathf.RoundToInt(c.g * 255f);
        uint b = (uint)Mathf.RoundToInt(c.b * 255f);
        return (r << 16) | (g << 8) | b;
    }

    // Knuth's multiplicative hash → wrap to [0, 2π). Same color always gets the
    // same phase across stages, so a "green blob" rustles consistently as the
    // tree grows through its sprite cells.
    static float PhaseFromColor(uint rgb) {
        unchecked {
            uint h = rgb * 2654435761u;
            return (h & 0xFFFFFFu) / (float)0xFFFFFF * 2f * Mathf.PI;
        }
    }

    // Pure black or pure white in the mask → blob is recognised but doesn't
    // sway. Reserved sentinels for "static foliage" — pick any other colour
    // to mark an animating blob.
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
            imp.spritePixelsPerUnit = 16;
            imp.filterMode          = FilterMode.Point;
            imp.textureCompression  = TextureImporterCompression.Uncompressed;
            imp.wrapMode            = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }
    }
}
