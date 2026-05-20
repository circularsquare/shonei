using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;
using static EditorUtilities;

// Right-click any Texture2D → "Generate Sprite Normal Map"  (single)
// Tools menu             → "Generate All Sprite Normal Maps" (batch, Assets/Sprites/)
// Tools menu             → "Generate All Sprite Normal Maps (Force)" (ignores cache)
//
// Each _n.png is auto-assigned as a _NormalMap secondary texture on the source
// sprite so URP's Sprite Lit shader picks it up with no runtime code needed.
// This works correctly for sprite sheets / animated sprites too.
//
// Slice awareness:
//   - Multi-sliced textures (spriteImportMode == Multiple) are processed PER SLICE
//     by default. Each slice is treated as if it were a standalone sprite — pixels
//     just outside the slice rect are seen as transparent, so frame boundaries get
//     proper edge bevels. This is what animation strips (powershaft, powershaftturn,
//     powershaft4) want.
//   - Spatial sheets (e.g. elevator/platform stacks) want the OPPOSITE: slices abut
//     in the world, so their shared boundaries should be interior. Set the merged
//     flag via `Assets → Toggle Merged Normals` (writes `normals=merged` into the
//     importer's userData). Merged sheets are processed as one big sprite; each
//     slice samples its own sub-region of the resulting normal map at runtime.
//
// Companion conventions:
//   `{stem}_e.png` — emission mask. Wired as `_EmissionMap` secondary texture
//       on `{stem}.png`. EmissionWriter.shader samples alpha at lighting time
//       so emissive pixels stay bright through the LightComposite multiply.
//   `{stem}_f.png` — fire art sprite. NOT processed for normal maps (skipped in
//       batch). Wired as its own `_EmissionMap` (self-reference, all visible
//       pixels emit) unless a `{stem}_e.png` companion overrides. Rendered at
//       runtime as a toggleable child GO on the parent structure — see
//       Structure.cs and LightSource.cs.
//   `{stem}_n.png` — generated normal map output (skipped in batch).
//   `{stem}_nm.png` — manual normal map override, authored by hand. When
//       present, the generator skips the edge-detect / flat-fill pass for
//       `{stem}.png` and wires `_nm.png` as the `_NormalMap` secondary instead
//       of `_n.png`. The auto-generated `_n.png` is left in place as a
//       reference; only the wiring changes. Skipped in batch.
//   `{stem}_h.png` — grayscale height map, authored by hand. When present
//       (and `_nm.png` is not), the generator builds `{stem}_n.png` from
//       height gradients via central difference instead of edge detection:
//       mid-gray=flat, brighter=raised, darker=recessed. Way more intuitive
//       than eyedropping normal-vector RGB values, and softening a slope is
//       just blurring the height map. Doesn't apply to fire sprites
//       (`_f.png` stays flat unless overridden via `_nm.png`). Skipped in
//       batch. Precedence: `_nm.png` > `_h.png` > auto edge-detect.
//   `{stem}_sway.png` — plant wind-sway mask. Wired as `_SwayMask` secondary
//       texture on `{stem}.png`. R-channel = per-pixel sway weight (0=rigid,
//       1=full). Plant.cs detects the secondary's presence at runtime and
//       flips the SR into mask-mode (fragment UV displacement instead of
//       vertex bend). See PlantSprite.shader / NormalsCapture.shader.
//
// Performance:
//   - Source pixels are read via `Texture2D.LoadImage(SysFile.ReadAllBytes(...))`
//     instead of toggling the importer's `isReadable` flag. Avoids two full
//     texture reimports per sprite.
//   - Batch runs wrap their work in `AssetDatabase.StartAssetEditing()` so that
//     ImportAsset / SaveAndReimport calls coalesce into one final import sweep
//     at the end, instead of N synchronous reimports inside the loop.
//   - Up-to-date check skips sprites whose source PNG, slice config, merged flag,
//     and `_e.png` companion all match the state recorded in the importer's
//     userData (`normalsCacheKey=<md5>`). Force variant ignores the cache.
//   - Single-asset and folder menus default to force-regen — the user explicitly
//     selected what to regenerate. Only the "All" variant honors the cache, with
//     a separate "(Force)" entry for full regen.
//
// BevelZ: higher = shallower bevel (more frontal catch), 1 = 45° bevel.
public static class SpriteNormalMapGenerator {
    const float BevelZ = 1f;

    // ── single selected texture ──────────────────────────────────────────────
    [MenuItem("Assets/Generate Sprite Normal Map", validate = true)]
    static bool Validate() {
        foreach (Object o in Selection.objects)
            if (o is Texture2D) return true;
        return false;
    }

    [MenuItem("Assets/Generate Sprite Normal Map")]
    static void Generate() {
        // Two-phase: see ProcessFolders for rationale. Phase 1 writes the _n.png
        // and configures its importer; Phase 2 wires secondaries on the source.
        // Splitting the phases is what lets `LoadAssetAtPath` see the new _n.png
        // when wiring runs — single-phase under StartAssetEditing returns null.
        var wired = new List<string>();
        try {
            AssetDatabase.StartAssetEditing();
            foreach (Object obj in Selection.objects)
                if (obj is Texture2D tex) {
                    string srcPath = AssetDatabase.GetAssetPath(tex);
                    if (ProcessTexture(tex, force: true)) wired.Add(srcPath);
                }
        } finally {
            AssetDatabase.StopAssetEditing();
        }

        try {
            AssetDatabase.StartAssetEditing();
            foreach (string srcPath in wired) WireSecondariesAndStamp(srcPath, fire: false);
        } finally {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }
    }

    // ── folder: right-click a folder to process all textures in it ──────────
    [MenuItem("Assets/Generate Normal Maps in Folder", validate = true)]
    static bool ValidateFolder() {
        foreach (Object o in Selection.objects) {
            string path = AssetDatabase.GetAssetPath(o);
            if (AssetDatabase.IsValidFolder(path)) return true;
        }
        return false;
    }

    [MenuItem("Assets/Generate Normal Maps in Folder")]
    static void GenerateFolder() {
        var folders = new List<string>();
        foreach (Object o in Selection.objects) {
            string path = AssetDatabase.GetAssetPath(o);
            if (AssetDatabase.IsValidFolder(path)) folders.Add(path);
        }
        var (processed, skipped, cancelled) = ProcessFolders(folders.ToArray(), force: false);
        AssetDatabase.Refresh();
        Debug.Log($"[NormalMapGen] Folder done — processed {processed}, skipped {skipped} up-to-date, in {folders.Count} folder(s).{(cancelled ? " (cancelled)" : "")}");
    }

    // ── batch: all textures under Assets/Sprites that aren't already _n ──────
    static readonly string[] BatchFolders = {
        "Assets/Resources/Sprites/Animals",
        "Assets/Resources/Sprites/Buildings",
        "Assets/Resources/Sprites/Items",
        "Assets/Resources/Sprites/Plants",
    };

    [MenuItem("Tools/Generate All Sprite Normal Maps")]
    internal static void GenerateAll() => GenerateAllInternal(force: false);

    [MenuItem("Tools/Generate All Sprite Normal Maps (Force)")]
    internal static void GenerateAllForce() => GenerateAllInternal(force: true);

    static void GenerateAllInternal(bool force) {
        var (processed, skipped, cancelled) = ProcessFolders(BatchFolders, force);
        AssetDatabase.Refresh();
        Debug.Log($"[NormalMapGen] Batch done — processed {processed}, skipped {skipped} up-to-date.{(force ? " (force)" : "")}{(cancelled ? " (cancelled)" : "")}");
    }

    // Returns (processed, skipped, cancelled).
    //
    // Two-phase to work around a StartAssetEditing pitfall: ImportAsset calls
    // for newly-created files are deferred until StopAssetEditing. If we tried
    // to wire secondary textures inside the same batch, AssetDatabase.LoadAssetAtPath
    // would return null for the just-written _n.png and AssignSecondaryTexture
    // would silently bail, leaving sprites without _NormalMap wiring (lights flat).
    //
    //   Phase 1: write _n.png + configure its importer.  (StartAssetEditing #1)
    //     ↓ StopAssetEditing flushes new asset records.
    //   Phase 2: wire _NormalMap/_EmissionMap on each source sprite + stamp
    //            cache key.                              (StartAssetEditing #2)
    static (int processed, int skipped, bool cancelled) ProcessFolders(string[] folders, bool force) {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", folders);
        int processed = 0, skipped = 0;
        bool cancelled = false;

        // Partition: regular sprites first, fire sprites second (post-pass for emission wiring).
        var regularPaths = new List<string>();
        var firePaths    = new List<string>();
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith("_n.png"))    continue;    // skip generated normal maps
            if (path.EndsWith("_nm.png"))   continue;    // skip manual normal map overrides
            if (path.EndsWith("_h.png"))    continue;    // skip height-map companions
            if (path.EndsWith("_e.png"))    continue;    // skip emission mask companions
            if (path.EndsWith("_sway.png")) continue;    // skip plant wind-sway mask companions
            if (path.Contains("/Sheets/"))  continue;    // skip source sheets — normals generated for split sprites only
            if (path.EndsWith("_f.png")) firePaths.Add(path);
            else                         regularPaths.Add(path);
        }
        int total = regularPaths.Count + firePaths.Count;

        // Track sources that had their _n.png (re)generated — these need
        // secondary-texture wiring in Phase 2.
        var wiredRegular = new List<string>();
        var wiredFire    = new List<string>();

        // Phase 1: generation.
        try {
            AssetDatabase.StartAssetEditing();
            int idx = 0;

            foreach (string path in regularPaths) {
                idx++;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Generating Normal Maps",
                        $"({idx}/{total}) {SysPath.GetFileName(path)}",
                        idx / (float)total)) {
                    cancelled = true; break;
                }
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null) continue;
                if (ProcessTexture(tex, force)) { processed++; wiredRegular.Add(path); }
                else                            { skipped++; }
            }

            // Post-pass: fire sprites get flat normal maps + self/_e emission wiring.
            if (!cancelled) {
                foreach (string path in firePaths) {
                    idx++;
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Generating Normal Maps (fire)",
                            $"({idx}/{total}) {SysPath.GetFileName(path)}",
                            idx / (float)total)) {
                        cancelled = true; break;
                    }
                    Texture2D fireTex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (fireTex == null) continue;
                    if (ProcessFlatNormal(fireTex, force)) { processed++; wiredFire.Add(path); }
                    else                                   { skipped++; }
                }
            }
        } finally {
            AssetDatabase.StopAssetEditing();
        }

        // Phase 2: wire secondaries + stamp cache keys. Coalesces N source-sprite
        // reimports under a fresh StartAssetEditing batch.
        try {
            AssetDatabase.StartAssetEditing();
            foreach (string srcPath in wiredRegular) WireSecondariesAndStamp(srcPath, fire: false);
            foreach (string srcPath in wiredFire)    WireSecondariesAndStamp(srcPath, fire: true);
        } finally {
            AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
        }

        return (processed, skipped, cancelled);
    }

    // ── flat normals (fire sprites) ─────────────────────────────────────────
    // Fire doesn't catch directional light — all opaque pixels get (0,0,1)
    // so NdotL is uniform regardless of sun/torch angle.
    //
    // Phase 1 only: writes _n.png + configures its importer. Caller runs
    // WireSecondariesAndStamp in Phase 2 to wire `_NormalMap` on the source.
    //
    // Returns true if processed, false if skipped (up-to-date).
    static bool ProcessFlatNormal(Texture2D source, bool force = false) {
        string srcPath = AssetDatabase.GetAssetPath(source);
        TextureImporter imp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
        if (imp == null) return false;

        if (!force && IsUpToDate(srcPath, imp)) return false;

        // Manual override: artist-authored `_nm.png` exists → skip flat-fill
        // generation. Phase 2 will wire `_nm.png` as `_NormalMap` instead of
        // `_n.png`. The auto-generated `_n.png` (if any) is left in place.
        string manualPathFlat = ManualNormalPathFor(srcPath);
        if (SysFile.Exists(manualPathFlat)) {
            ConfigureManualNormalImporter(manualPathFlat, imp);
            return true;
        }

        return WriteFlatNormal(srcPath, imp);
    }

    // Shared flat-fill writer used by ProcessFlatNormal (fire sprites) and the
    // liquid-storage branch of ProcessTexture. Decodes the source for alpha,
    // emits a `_n.png` where every opaque pixel is (0,0,1) normal-encoded.
    // Caller is responsible for any upstream up-to-date / override checks.
    static bool WriteFlatNormal(string srcPath, TextureImporter imp) {
        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!tex.LoadImage(SysFile.ReadAllBytes(srcPath))) {
            Debug.LogError($"[NormalMapGen] Failed to decode {srcPath}");
            Object.DestroyImmediate(tex);
            return false;
        }
        Color32[] src = tex.GetPixels32();
        int w = tex.width, h = tex.height;
        Object.DestroyImmediate(tex);

        Color32[] dst = new Color32[w * h];
        for (int i = 0; i < w * h; i++) {
            byte a = (byte)(src[i].a < 128 ? 0 : 255);
            dst[i] = new Color32(128, 128, 255, a);
        }

        Texture2D normalTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        normalTex.SetPixels32(dst);
        normalTex.Apply();
        string outPath = NormalPathFor(srcPath);
        SysFile.WriteAllBytes(outPath, normalTex.EncodeToPNG());
        Object.DestroyImmediate(normalTex);

        AssetDatabase.ImportAsset(outPath);
        TextureImporter nImp = AssetImporter.GetAtPath(outPath) as TextureImporter;
        if (nImp != null) {
            nImp.textureType        = TextureImporterType.Default;
            nImp.textureCompression = TextureImporterCompression.Uncompressed;
            nImp.filterMode         = imp.filterMode;
            nImp.wrapMode           = TextureWrapMode.Clamp;
            nImp.SaveAndReimport();
        }

        Debug.Log($"[NormalMapGen] Written (flat): {outPath}");
        return true;
    }

    // ── shared I/O helpers ───────────────────────────────────────────────────
    // Decode a PNG into a Color32[] via Texture2D.LoadImage — avoids the
    // imp.isReadable=true reimport flip the old path used. `label` is purely
    // diagnostic ("source", "height map"). Returns false on decode failure.
    static bool LoadPixels(string path, string label, out Color32[] pixels, out int w, out int h) {
        pixels = null; w = 0; h = 0;
        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!tex.LoadImage(SysFile.ReadAllBytes(path))) {
            Debug.LogError($"[NormalMapGen] Failed to decode {label} {path}");
            Object.DestroyImmediate(tex);
            return false;
        }
        pixels = tex.GetPixels32();
        w = tex.width;
        h = tex.height;
        Object.DestroyImmediate(tex);
        return true;
    }

    // Encode `dst` to {srcPath}'s `_n.png` companion and configure its importer.
    // `sourceImp` supplies the filterMode to match. `logSuffix` is appended to the
    // success log line (e.g. " (height)") so the two generation paths stay
    // distinguishable in the editor console.
    static void WriteNormalMap(string srcPath, Color32[] dst, int w, int h, TextureImporter sourceImp, string logSuffix) {
        Texture2D normalTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        normalTex.SetPixels32(dst);
        normalTex.Apply();

        string outPath = NormalPathFor(srcPath);
        SysFile.WriteAllBytes(outPath, normalTex.EncodeToPNG());
        Object.DestroyImmediate(normalTex);

        AssetDatabase.ImportAsset(outPath);
        TextureImporter nImp = AssetImporter.GetAtPath(outPath) as TextureImporter;
        if (nImp != null) {
            // Use Default (not NormalMap) so the texture stays as plain RGBA32 —
            // the same packed 0-1 format that TileSpriteCache bakes and NormalsCapture decodes.
            nImp.textureType        = TextureImporterType.Default;
            nImp.textureCompression = TextureImporterCompression.Uncompressed;
            nImp.filterMode         = sourceImp.filterMode;
            nImp.wrapMode           = TextureWrapMode.Clamp;
            nImp.SaveAndReimport();
        }

        Debug.Log($"[NormalMapGen] Written{logSuffix}: {outPath}");
    }

    // ── core ─────────────────────────────────────────────────────────────────
    // Phase 1 only: writes _n.png + configures its importer. Caller runs
    // WireSecondariesAndStamp in Phase 2 to wire `_NormalMap` (and optionally
    // `_EmissionMap`) on the source sprite.
    //
    // Returns true if processed, false if skipped (up-to-date).
    static bool ProcessTexture(Texture2D source, bool force = false) {
        string srcPath = AssetDatabase.GetAssetPath(source);
        TextureImporter imp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
        if (imp == null) return false;

        if (!force && IsUpToDate(srcPath, imp)) return false;

        // Manual override: artist-authored `_nm.png` exists → skip edge-detect
        // generation. Phase 2 will wire `_nm.png` as `_NormalMap` instead of
        // `_n.png`. The auto-generated `_n.png` (if any) is left in place as a
        // reference for the artist.
        string manualPath = ManualNormalPathFor(srcPath);
        if (SysFile.Exists(manualPath)) {
            ConfigureManualNormalImporter(manualPath, imp);
            return true;
        }

        // Height-map source: artist-authored `_h.png` exists → derive `_n.png`
        // from height gradients (central difference) instead of edge detection.
        // The result still goes to `{stem}_n.png` and is wired the usual way in
        // Phase 2 — only the generation algorithm differs.
        string heightPath = HeightMapPathFor(srcPath);
        if (SysFile.Exists(heightPath)) {
            ConfigureHeightMapImporter(heightPath);
            return ProcessHeightToNormal(srcPath, heightPath, imp);
        }

        // Liquid in storage: render as a flat pool (uniform (0,0,1) normals)
        // regardless of silhouette. The `floor` variant of the same item is
        // skipped by IsLiquidStorageSprite — it gets normal item treatment
        // because the artist draws it as a bucket.
        if (IsLiquidStorageSprite(srcPath)) {
            return WriteFlatNormal(srcPath, imp);
        }

        if (!LoadPixels(srcPath, "source", out Color32[] src, out int w, out int h)) return false;
        Color32[] dst = new Color32[w * h];

        // Default-fill: transparent fallback. Pixels outside any processed rect
        // (e.g. dead-zones in non-tiling sliced atlases) read as "no sprite" in
        // the lighting pipeline.
        for (int i = 0; i < w * h; i++) dst[i] = new Color32(128, 128, 255, 0);

        // Decide which rect(s) to process. Multi-sliced textures get per-slice
        // edge detection by default — frame boundaries are real edges. Set the
        // merged flag (`Assets → Toggle Merged Normals`) when slices represent
        // spatial neighbours that abut in the world (elevator/platform stacks).
        var rects = new List<RectInt>();
        bool merged = HasUserDataFlag(imp, "normals", "merged");
        if (imp.spriteImportMode == SpriteImportMode.Multiple && !merged && imp.spritesheet.Length > 0) {
            foreach (SpriteMetaData md in imp.spritesheet) {
                rects.Add(new RectInt(
                    Mathf.RoundToInt(md.rect.x),
                    Mathf.RoundToInt(md.rect.y),
                    Mathf.RoundToInt(md.rect.width),
                    Mathf.RoundToInt(md.rect.height)));
            }
        } else {
            rects.Add(new RectInt(0, 0, w, h));
        }

        // Interior-edge suppression: sprites that rest on / stack against
        // something else at runtime shouldn't bevel where they meet it. Plants
        // multi-tile, buildings/items at their bottom row, etc. See GetInteriorEdges.
        var (interiorBottom, interiorTop) = GetInteriorEdges(srcPath);

        foreach (RectInt r in rects) {
            int xMin = r.xMin, xMax = r.xMax, yMin = r.yMin, yMax = r.yMax;
            for (int y = yMin; y < yMax; y++) {
                for (int x = xMin; x < xMax; x++) {
                    int i = y * w + x;
                    if (src[i].a < 128) { dst[i] = new Color32(128, 128, 255, 0); continue; }

                    // Edges are: rect boundary OR a transparent neighbour. Off-rect
                    // pixels are treated as transparent regardless of their actual
                    // alpha, which is what isolates per-slice processing.
                    // interiorBottom/interiorTop suppress the rect-bottom / rect-top
                    // case for sprites we know stack against another tile at runtime.
                    bool eL = x == xMin       || src[y * w + (x - 1)].a < 128;
                    bool eR = x == xMax - 1   || src[y * w + (x + 1)].a < 128;
                    bool eD = (y == yMin)     ? !interiorBottom : src[(y - 1) * w + x].a < 128;
                    bool eU = (y == yMax - 1) ? !interiorTop    : src[(y + 1) * w + x].a < 128;

                    float nx = (eR ? 1f : 0f) - (eL ? 1f : 0f);
                    float ny = (eU ? 1f : 0f) - (eD ? 1f : 0f);
                    float nz = BevelZ;

                    float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 0f) { nx /= len; ny /= len; nz /= len; }
                    else          { nx = 0f; ny = 0f; nz = 1f; }

                    dst[i] = new Color32(
                        (byte)(nx * 127.5f + 128f),
                        (byte)(ny * 127.5f + 128f),
                        (byte)(nz * 127.5f + 128f),
                        255
                    );
                }
            }
        }

        WriteNormalMap(srcPath, dst, w, h, imp, logSuffix: "");
        return true;
    }

    // ── height-map → normal ──────────────────────────────────────────────────
    // Derive `_n.png` from a grayscale `_h.png` companion via central
    // difference. The artist paints depth (intuitive — light=raised, dark=
    // recessed, mid-gray=flat) and the generator turns it into a normal map.
    // Softening a slope is just blurring the height map.
    //
    // Strength scales the gradient before normalization: higher = more
    // aggressive tilt for the same height delta. 2.0 is a soft pixel-art
    // default; bump contrast on the height map if you need more drama.
    //
    // Per-slice handling is intentionally *not* applied here. Unlike
    // edge-detect (where slice boundaries are real silhouette edges), height
    // maps are inherently continuous — clamping at image bounds is enough.
    // If a multi-sliced sheet needs per-slice gradients, paint the height map
    // with explicit mid-gray gutters between slices.
    //
    // Phase 1 only: writes `_n.png` + configures its importer. Caller runs
    // WireSecondariesAndStamp in Phase 2.
    static bool ProcessHeightToNormal(string srcPath, string heightPath, TextureImporter imp) {
        const float Strength = 2.0f;

        // Source pixels feed the alpha mask; off-sprite pixels produce transparent
        // normals just like edge-detect.
        if (!LoadPixels(srcPath, "source", out Color32[] src, out int w, out int h)) return false;
        if (!LoadPixels(heightPath, "height map", out Color32[] height, out int hw, out int hh)) return false;
        if (hw != w || hh != h) {
            Debug.LogError($"[NormalMapGen] Height map {heightPath} ({hw}×{hh}) doesn't match source {srcPath} ({w}×{h}). Skipping.");
            return false;
        }

        // Match auto edge-detect's bevel-at-silhouette behavior: out-of-bounds
        // samples read as 0 (black/transparent), producing a strong gradient
        // and a bevel at the image edge. EXCEPT at "interior" edges, where the
        // sprite meets another at runtime — there we clamp so the gradient
        // reads as flat. See GetInteriorEdges for which edges count as interior.
        var (interiorBottom, interiorTop) = GetInteriorEdges(srcPath);

        Color32[] dst = new Color32[w * h];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int i = y * w + x;
                if (src[i].a < 128) { dst[i] = new Color32(128, 128, 255, 0); continue; }

                float hC = height[y * w + x].r / 255f;
                float hL = x > 0     ? height[y * w + (x - 1)].r / 255f : 0f;
                float hR = x < w - 1 ? height[y * w + (x + 1)].r / 255f : 0f;
                float hD = y > 0     ? height[(y - 1) * w + x].r / 255f
                                     : (interiorBottom ? hC : 0f);
                float hU = y < h - 1 ? height[(y + 1) * w + x].r / 255f
                                     : (interiorTop    ? hC : 0f);

                // Surface tangent right = (1, 0, hR-hL); tangent up = (0, 1, hU-hD).
                // Cross product → normal (-dx, -dy, 1) before normalize.
                float nx = -(hR - hL) * Strength;
                float ny = -(hU - hD) * Strength;
                float nz = 1f;

                float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0f) { nx /= len; ny /= len; nz /= len; }
                else          { nx = 0f; ny = 0f; nz = 1f; }

                dst[i] = new Color32(
                    (byte)(nx * 127.5f + 128f),
                    (byte)(ny * 127.5f + 128f),
                    (byte)(nz * 127.5f + 128f),
                    255
                );
            }
        }

        WriteNormalMap(srcPath, dst, w, h, imp, logSuffix: " (height)");
        return true;
    }

    // ── phase-2 wiring ───────────────────────────────────────────────────────
    // Wires `_NormalMap` (always) and `_EmissionMap` (regular sprites only,
    // based on `_e.png` companion existence) as secondary textures on the
    // source sprite, then stamps the importer's `normalsCacheKey` userData so
    // future runs can skip up-to-date sprites.
    //
    // Must run AFTER Phase 1's StopAssetEditing — see ProcessFolders for the
    // full rationale. The short version: AssetDatabase.LoadAssetAtPath returns
    // null for the freshly-written _n.png while it's still queued for import,
    // and AssignSecondaryTexture LogErrors and bails in that case.
    static void WireSecondariesAndStamp(string srcPath, bool fire) {
        TextureImporter imp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
        if (imp == null) return;

        // Manual `_nm.png` takes precedence over the auto-generated `_n.png`
        // when wiring `_NormalMap`. The auto file is left untouched on disk
        // (handy for diffing against the manual override) but isn't referenced
        // by the source sprite while the override exists.
        string manualPath = ManualNormalPathFor(srcPath);
        string normalPath = SysFile.Exists(manualPath) ? manualPath : NormalPathFor(srcPath);
        AssignSecondaryTexture(srcPath, normalPath, "_NormalMap");

        if (!fire) {
            // Wire/unwire `_EmissionMap` based on companion existence. Removing
            // the companion later should also remove the secondary entry —
            // otherwise the importer keeps a dead reference.
            string ePath = EmissionPathFor(srcPath);
            if (SysFile.Exists(ePath)) AssignSecondaryTexture(srcPath, ePath, "_EmissionMap");
            else                       RemoveSecondaryTexture(srcPath, "_EmissionMap");

            // Same pattern for plant wind-sway masks. Plant.cs detects the
            // `_SwayMask` secondary's presence at runtime to flip the renderer
            // into mask-mode (per-pixel UV displacement instead of vertex bend).
            string swayPath = SwayPathFor(srcPath);
            if (SysFile.Exists(swayPath)) AssignSecondaryTexture(srcPath, swayPath, "_SwayMask");
            else                          RemoveSecondaryTexture(srcPath, "_SwayMask");
        }

        // Stamp cache key. AssignSecondaryTexture's SaveAndReimport calls plus
        // this trailing one all coalesce under the caller's StartAssetEditing —
        // net cost is one importer pass per sprite.
        SetUserDataFlag(imp, "normalsCacheKey", ComputeCacheKey(srcPath, imp));
        imp.SaveAndReimport();
    }

    // ── interior-edge rules (per category) ───────────────────────────────────
    // An "interior" edge means the sprite touches something at that boundary
    // at runtime, so the auto edge-detect should NOT bevel there (the bevel
    // would read as a seam between the sprite and the thing it's resting on).
    // For height-map gradients the same rule applies: at an interior edge, we
    // clamp the height sample instead of treating out-of-bounds as 0, so the
    // gradient at the boundary reads as zero (flat) rather than a drop.
    //
    // Category rules:
    //   - Plants under Plants/: bottom always interior (every plant sits on
    //     terrain — including saplings). Top depends on the multi-tile
    //     stacking rule below.
    //   - Buildings under Buildings/: bottom always interior (rests on
    //     terrain). Top is a real silhouette edge.
    //   - Items under Items/: bottom always interior (in-storage variants
    //     sit on a shelf inside a box; the floor variant sits on the floor —
    //     either way the bottom is at the resting surface).
    //   - Animals under Animals/: no interior edges (animals are heavily
    //     animated, body parts can be at any silhouette position).
    //   - Anything else: no interior edges.
    //
    // Plant top rule (within Plants/Split/<name>/), derived from how
    // Plant.UpdateSprite picks anchor/extension sprites:
    //   g4  → mid-trunk segment sandwiched between tiles above and below.
    //   b4  → anchor when extensions exist; another tile sits above.
    //   anything else → top is a real silhouette edge (canopy or sapling).
    static (bool bottom, bool top) GetInteriorEdges(string srcPath) {
        if (string.IsNullOrEmpty(srcPath)) return (false, false);
        bool inPlants    = srcPath.IndexOf("/Plants/",    System.StringComparison.OrdinalIgnoreCase) >= 0;
        bool inBuildings = srcPath.IndexOf("/Buildings/", System.StringComparison.OrdinalIgnoreCase) >= 0;
        bool inItems     = srcPath.IndexOf("/Items/",     System.StringComparison.OrdinalIgnoreCase) >= 0;
        if (inPlants)    return (bottom: true, top: GetPlantTopInterior(srcPath));
        if (inBuildings) return (bottom: true, top: false);
        if (inItems)     return (bottom: true, top: false);
        return (false, false);
    }

    static bool GetPlantTopInterior(string srcPath) {
        if (srcPath.IndexOf("/Plants/Split/", System.StringComparison.OrdinalIgnoreCase) < 0) return false;
        string stem = SysPath.GetFileNameWithoutExtension(srcPath);
        return stem == "g4" || stem == "b4";
    }

    // ── liquids ──────────────────────────────────────────────────────────────
    // Liquids in storage render as a flat pool in the container — no bevels
    // anywhere. Only the `floor.png` variant (which the artist draws as a
    // bucket) gets the normal item treatment. Source of truth for "is this
    // item a liquid" is itemsDb.json's `itemClass: "liquid"` field.
    //
    // `floor` and `icon` are intentionally treated differently: floor is the
    // in-world bucket sprite, icon is the UI representation which still reads
    // as a puddle so it's safe to flatten.
    static HashSet<string> _liquidItemsCache;
    const string ItemsDbPath = "Assets/Resources/itemsDb.json";

    static bool IsLiquidStorageSprite(string srcPath) {
        if (string.IsNullOrEmpty(srcPath)) return false;
        if (srcPath.IndexOf("/Items/split/", System.StringComparison.OrdinalIgnoreCase) < 0) return false;
        string stem = SysPath.GetFileNameWithoutExtension(srcPath);
        if (string.Equals(stem, "floor", System.StringComparison.OrdinalIgnoreCase)) return false;
        string dir = SysPath.GetDirectoryName(srcPath);
        if (string.IsNullOrEmpty(dir)) return false;
        string itemName = SysPath.GetFileName(dir);
        return GetLiquidItems().Contains(itemName);
    }

    static HashSet<string> GetLiquidItems() {
        if (_liquidItemsCache != null) return _liquidItemsCache;
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        try {
            string text = SysFile.ReadAllText(ItemsDbPath);
            var root = Newtonsoft.Json.Linq.JToken.Parse(text);
            WalkItemsForLiquid(root, set);
        } catch (System.Exception ex) {
            Debug.LogError($"[NormalMapGen] Failed to read {ItemsDbPath} for liquid detection: {ex.Message}");
        }
        _liquidItemsCache = set;
        return set;
    }

    static void WalkItemsForLiquid(Newtonsoft.Json.Linq.JToken node, HashSet<string> liquids) {
        if (node is Newtonsoft.Json.Linq.JArray arr) {
            foreach (var c in arr) WalkItemsForLiquid(c, liquids);
            return;
        }
        if (node is Newtonsoft.Json.Linq.JObject obj) {
            var classTok = obj["itemClass"];
            var nameTok  = obj["name"];
            if (classTok != null && (string)classTok == "liquid" && nameTok != null) {
                liquids.Add((string)nameTok);
            }
            var children = obj["children"];
            if (children != null) WalkItemsForLiquid(children, liquids);
        }
    }

    // ── path helpers ─────────────────────────────────────────────────────────
    static string NormalPathFor(string srcPath) {
        string dir  = SysPath.GetDirectoryName(srcPath);
        string stem = SysPath.GetFileNameWithoutExtension(srcPath);
        return SysPath.Combine(dir, stem + "_n.png").Replace('\\', '/');
    }

    static string EmissionPathFor(string srcPath) {
        string dir  = SysPath.GetDirectoryName(srcPath);
        string stem = SysPath.GetFileNameWithoutExtension(srcPath);
        return SysPath.Combine(dir, stem + "_e.png").Replace('\\', '/');
    }

    static string SwayPathFor(string srcPath) {
        string dir  = SysPath.GetDirectoryName(srcPath);
        string stem = SysPath.GetFileNameWithoutExtension(srcPath);
        return SysPath.Combine(dir, stem + "_sway.png").Replace('\\', '/');
    }

    static string ManualNormalPathFor(string srcPath) {
        string dir  = SysPath.GetDirectoryName(srcPath);
        string stem = SysPath.GetFileNameWithoutExtension(srcPath);
        return SysPath.Combine(dir, stem + "_nm.png").Replace('\\', '/');
    }

    static string HeightMapPathFor(string srcPath) {
        string dir  = SysPath.GetDirectoryName(srcPath);
        string stem = SysPath.GetFileNameWithoutExtension(srcPath);
        return SysPath.Combine(dir, stem + "_h.png").Replace('\\', '/');
    }

    // Configure a height-map's importer so it doesn't pollute the Project view
    // with spurious sprite slicing. Heights are read off-disk via LoadImage so
    // the importer's actual texture pipeline is irrelevant — this is purely
    // cosmetic / "don't accidentally try to use this as a sprite."
    static void ConfigureHeightMapImporter(string heightPath) {
        TextureImporter hImp = AssetImporter.GetAtPath(heightPath) as TextureImporter;
        if (hImp == null) return;
        bool changed = false;
        if (hImp.textureType        != TextureImporterType.Default)         { hImp.textureType        = TextureImporterType.Default;         changed = true; }
        if (hImp.textureCompression != TextureImporterCompression.Uncompressed) { hImp.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
        if (changed) hImp.SaveAndReimport();
    }

    // Configure a manual normal map's importer to match what the generator
    // would set on an auto `_n.png`: plain RGBA32 (no compression), clamp wrap,
    // filter mode inherited from the source sprite. Called when a `_nm.png` is
    // first picked up so the artist doesn't have to set these by hand.
    static void ConfigureManualNormalImporter(string manualPath, TextureImporter sourceImp) {
        TextureImporter nImp = AssetImporter.GetAtPath(manualPath) as TextureImporter;
        if (nImp == null) return;
        bool changed = false;
        if (nImp.textureType        != TextureImporterType.Default)         { nImp.textureType        = TextureImporterType.Default;         changed = true; }
        if (nImp.textureCompression != TextureImporterCompression.Uncompressed) { nImp.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
        if (nImp.wrapMode           != TextureWrapMode.Clamp)               { nImp.wrapMode           = TextureWrapMode.Clamp;               changed = true; }
        if (sourceImp != null && nImp.filterMode != sourceImp.filterMode)   { nImp.filterMode         = sourceImp.filterMode;                changed = true; }
        if (changed) nImp.SaveAndReimport();
    }

    // ── up-to-date check ─────────────────────────────────────────────────────
    // Inputs that should invalidate a cached `_n.png`:
    //   - source PNG content (mtime proxy)
    //   - merged flag
    //   - sprite import mode (Single ↔ Multiple)
    //   - filter mode (propagates into _n.png importer settings)
    //   - spritesheet rects + names (slice changes regenerate)
    //   - companion `_e.png` existence + mtime (affects _EmissionMap wiring)
    //   - companion `_nm.png` existence + mtime (manual override; flips
    //     wiring from auto `_n.png` to the artist-authored manual file)
    //   - companion `_h.png` existence + mtime (height-map source; switches
    //     the generation algorithm from edge-detect to gradient-from-height)
    // Hashed and stored as `normalsCacheKey=<md5>` in the source's importer userData.
    //
    // "Output exists" check accepts either the auto `_n.png` OR the manual
    // `_nm.png` — when the artist adds a manual file before any auto-gen has
    // run, we still want a successful up-to-date verdict after one round.
    static bool IsUpToDate(string srcPath, TextureImporter imp) {
        if (!SysFile.Exists(NormalPathFor(srcPath)) && !SysFile.Exists(ManualNormalPathFor(srcPath))) return false;
        string stored = GetUserDataValue(imp, "normalsCacheKey");
        if (string.IsNullOrEmpty(stored)) return false;
        return stored == ComputeCacheKey(srcPath, imp);
    }

    static string ComputeCacheKey(string srcPath, TextureImporter imp) {
        var sb = new StringBuilder();
        sb.Append(SysFile.GetLastWriteTimeUtc(srcPath).Ticks);
        sb.Append('|').Append(HasUserDataFlag(imp, "normals", "merged") ? "M" : "_");
        sb.Append('|').Append(imp.spriteImportMode);
        sb.Append('|').Append(imp.filterMode);
        if (imp.spriteImportMode == SpriteImportMode.Multiple) {
            foreach (SpriteMetaData md in imp.spritesheet) {
                sb.Append('|').Append(md.name)
                  .Append(':').Append(md.rect.x).Append(',').Append(md.rect.y)
                  .Append(',').Append(md.rect.width).Append(',').Append(md.rect.height);
            }
        }
        string ePath = EmissionPathFor(srcPath);
        if (SysFile.Exists(ePath)) {
            sb.Append("|E").Append(SysFile.GetLastWriteTimeUtc(ePath).Ticks);
        }
        string swayPath = SwayPathFor(srcPath);
        if (SysFile.Exists(swayPath)) {
            sb.Append("|S").Append(SysFile.GetLastWriteTimeUtc(swayPath).Ticks);
        }
        // Manual normal override flips wiring; presence + mtime invalidates the
        // cache so the artist can iterate on `_nm.png` and see it re-wired.
        string manualNormalPath = ManualNormalPathFor(srcPath);
        if (SysFile.Exists(manualNormalPath)) {
            sb.Append("|N").Append(SysFile.GetLastWriteTimeUtc(manualNormalPath).Ticks);
        }
        // Height-map source flips the generation algorithm; iterate on `_h.png`
        // and the gradient-derived `_n.png` regenerates on the next batch.
        string heightMapPath = HeightMapPathFor(srcPath);
        if (SysFile.Exists(heightMapPath)) {
            sb.Append("|H").Append(SysFile.GetLastWriteTimeUtc(heightMapPath).Ticks);
        }
        // Interior-edge rule: include the resolved flags so cached normal
        // maps invalidate when a sprite enters/leaves the rule (renamed file,
        // moved folder, or rule edits — bump anything in GetInteriorEdges).
        var (intB, intT) = GetInteriorEdges(srcPath);
        if (intB || intT) sb.Append("|I").Append(intB ? "B" : "_").Append(intT ? "T" : "_");
        // Liquid-storage flag flips the generator to flat-fill. Include
        // itemsDb.json's mtime for any sprite under Items/ so toggling an
        // item's `itemClass` invalidates the cached normal map.
        if (srcPath.IndexOf("/Items/", System.StringComparison.OrdinalIgnoreCase) >= 0) {
            if (SysFile.Exists(ItemsDbPath)) sb.Append("|D").Append(SysFile.GetLastWriteTimeUtc(ItemsDbPath).Ticks);
            if (IsLiquidStorageSprite(srcPath)) sb.Append("|L");
        }
        return Md5(sb.ToString());
    }

    // ── secondary-texture wiring ─────────────────────────────────────────────
    // Generic secondary-texture assignment. URP's Sprite Lit shader and our
    // own NormalsCapture / EmissionWriter shaders pick these up automatically
    // by name (`_NormalMap`, `_EmissionMap`, etc.) — no runtime wiring needed.
    static void AssignSecondaryTexture(string srcPath, string texPath, string propName) {
        TextureImporter imp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
        if (imp == null) return;

        Texture2D secTex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        if (secTex == null) { Debug.LogError($"[NormalMapGen] Could not load secondary texture: {texPath}"); return; }

        // Secondary textures live at m_SpriteSheet.m_SecondaryTextures in the importer
        var so   = new SerializedObject(imp);
        var arr  = so.FindProperty("m_SpriteSheet.m_SecondaryTextures");
        if (arr == null) return;

        // Remove any existing entry for this property name
        for (int i = arr.arraySize - 1; i >= 0; i--) {
            var entry = arr.GetArrayElementAtIndex(i);
            if (entry.FindPropertyRelative("name").stringValue == propName)
                arr.DeleteArrayElementAtIndex(i);
        }

        // Append new entry
        arr.arraySize++;
        var elem = arr.GetArrayElementAtIndex(arr.arraySize - 1);
        elem.FindPropertyRelative("name").stringValue                = propName;
        elem.FindPropertyRelative("texture").objectReferenceValue    = secTex;

        so.ApplyModifiedPropertiesWithoutUndo();
        imp.SaveAndReimport();
    }

    // Strip any secondary-texture entries with the given name. Used when an
    // `_e.png` companion has been deleted — without this, the importer would
    // keep a dead `_EmissionMap` reference.
    static void RemoveSecondaryTexture(string srcPath, string propName) {
        TextureImporter imp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
        if (imp == null) return;

        var so  = new SerializedObject(imp);
        var arr = so.FindProperty("m_SpriteSheet.m_SecondaryTextures");
        if (arr == null) return;

        bool changed = false;
        for (int i = arr.arraySize - 1; i >= 0; i--) {
            var entry = arr.GetArrayElementAtIndex(i);
            if (entry.FindPropertyRelative("name").stringValue == propName) {
                arr.DeleteArrayElementAtIndex(i);
                changed = true;
            }
        }
        if (changed) {
            so.ApplyModifiedPropertiesWithoutUndo();
            imp.SaveAndReimport();
        }
    }

    // userData + Md5 helpers live in EditorUtilities (used by all sheet splitters too).

    // ── menu: toggle merged-normals flag ─────────────────────────────────────
    // Spatial sheets (elevator, platform stacks) want the generator to process
    // the whole texture as one sprite so inter-tile boundaries stay interior.
    // Toggle the flag, regenerate.
    [MenuItem("Assets/Toggle Merged Normals", validate = true)]
    static bool ValidateToggleMerged() {
        foreach (Object o in Selection.objects)
            if (o is Texture2D) return true;
        return false;
    }

    [MenuItem("Assets/Toggle Merged Normals")]
    static void ToggleMerged() {
        foreach (Object obj in Selection.objects) {
            if (!(obj is Texture2D tex)) continue;
            string path = AssetDatabase.GetAssetPath(tex);
            TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) continue;
            bool was = HasUserDataFlag(imp, "normals", "merged");
            if (was) ClearUserDataFlag(imp, "normals");
            else     SetUserDataFlag(imp, "normals", "merged");
            imp.SaveAndReimport();
            Debug.Log($"[NormalMapGen] Merged normals {(was ? "OFF" : "ON")} for {path}. Re-run normal map generation.");
        }
    }

    // ── menu: slice a vertical building sheet into _b/_m/_t ──────────────────
    // For 16×N textures (N ∈ {32, 48}), sets up the importer's spritesheet with
    // bottom→top slices named `{stem}_b`, optional `{stem}_m`, `{stem}_t`, each
    // 16×16 with centred pivot (matches existing single-file convention used
    // by StructureVisuals.PositionFor / shape-aware extension SRs). Also turns
    // on the merged-normals flag — spatial stacks always want it.
    [MenuItem("Assets/Slice Vertical Building Sheet", validate = true)]
    static bool ValidateSliceVertical() {
        foreach (Object o in Selection.objects) {
            if (!(o is Texture2D tex)) continue;
            if (tex.width == 16 && (tex.height == 32 || tex.height == 48)) return true;
        }
        return false;
    }

    [MenuItem("Assets/Slice Vertical Building Sheet")]
    static void SliceVertical() {
        foreach (Object obj in Selection.objects) {
            if (!(obj is Texture2D tex)) continue;
            string path = AssetDatabase.GetAssetPath(tex);
            TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) continue;
            if (tex.width != 16 || (tex.height != 32 && tex.height != 48)) {
                Debug.LogError($"[NormalMapGen] {path}: vertical slicer expects 16×32 or 16×48 (got {tex.width}×{tex.height}).");
                continue;
            }

            // Slice names follow the existing suffix convention so
            // StructureVisuals.LoadShapeSprite can find them. Strip a trailing
            // `_s` from the file stem to get the canonical building name —
            // `platform_s.png` slices into `platform_b/_m/_t`, NOT
            // `platform_s_b/_m/_t`. The `_s` suffix is purely a filename
            // disambiguator (so the sheet can coexist with a 1×1 `{name}.png`).
            // For 16×48: bottom (y=0) → _b, middle (y=16) → _m, top (y=32) → _t.
            // For 16×32: bottom (y=0) → _b, top (y=16) → _t (no middle).
            string stem = SysPath.GetFileNameWithoutExtension(path);
            string buildingName = stem.EndsWith("_s") ? stem.Substring(0, stem.Length - 2) : stem;
            int rows = tex.height / 16;
            var sheet = new List<SpriteMetaData>(rows);
            for (int row = 0; row < rows; row++) {
                string suffix = row == 0 ? "_b"
                              : row == rows - 1 ? "_t"
                              : "_m";
                sheet.Add(new SpriteMetaData {
                    name      = buildingName + suffix,
                    rect      = new Rect(0, row * 16, 16, 16),
                    alignment = (int)SpriteAlignment.Center,
                    pivot     = new Vector2(0.5f, 0.5f),
                });
            }
            imp.spriteImportMode = SpriteImportMode.Multiple;
            imp.spritesheet      = sheet.ToArray();
            imp.spritePixelsPerUnit = 16;
            SetUserDataFlag(imp, "normals", "merged");

            // Clear m_NameFileIdTable so Unity allocates fresh unique internalIDs
            // for the new slice names. Without this, re-slicing leaves stale
            // entries from prior names alongside new entries that all default to
            // 0, producing "Identifier uniqueness violation" warnings and
            // ambiguous asset references.
            var so = new SerializedObject(imp);
            var table = so.FindProperty("m_SpriteSheet.m_NameFileIdTable");
            if (table != null && table.isArray) {
                table.ClearArray();
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            imp.SaveAndReimport();
            Debug.Log($"[NormalMapGen] Sliced {path} into {rows} rows (merged normals ON). Re-run normal map generation.");
        }
    }
}
