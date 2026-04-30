using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;

// Right-click any Texture2D → "Generate Sprite Normal Map"  (single)
// Tools menu             → "Generate All Sprite Normal Maps" (batch, Assets/Sprites/)
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
        foreach (Object obj in Selection.objects)
            if (obj is Texture2D tex)
                ProcessTexture(tex);
        AssetDatabase.Refresh();
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
        int count = ProcessFolders(folders.ToArray());
        AssetDatabase.Refresh();
        Debug.Log($"[NormalMapGen] Done — processed {count} texture(s) in {folders.Count} folder(s).");
    }

    // ── batch: all textures under Assets/Sprites that aren't already _n ──────
    static readonly string[] BatchFolders = {
        "Assets/Resources/Sprites/Animals",
        "Assets/Resources/Sprites/Buildings",
        "Assets/Resources/Sprites/Items",
        "Assets/Resources/Sprites/Plants",
    };

    [MenuItem("Tools/Generate All Sprite Normal Maps")]
    internal static void GenerateAll() {
        int count = ProcessFolders(BatchFolders);
        AssetDatabase.Refresh();
        Debug.Log($"[NormalMapGen] Done — processed {count} texture(s).");
    }

    static int ProcessFolders(string[] folders) {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", folders);
        int count = 0;
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith("_n.png")) continue;       // skip generated normal maps
            if (path.EndsWith("_f.png")) continue;       // fire sprites get flat normals in the post-pass below
            if (path.EndsWith("_e.png")) continue;       // skip emission mask companions
            if (path.Contains("/Sheets/")) continue;     // skip source sheets — normals generated for split sprites only
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex != null) { ProcessTexture(tex); count++; }
        }

        // Post-pass: fire art sprites (_f.png) get flat normal maps (fire doesn't
        // catch directional light) and self-referencing _EmissionMap (all pixels emit).
        foreach (string guid in guids) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith("_f.png")) continue;
            Texture2D fireTex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (fireTex != null) { ProcessFlatNormal(fireTex); count++; }
            string dir  = SysPath.GetDirectoryName(path);
            string stem = SysPath.GetFileNameWithoutExtension(path);
            string ePath = SysPath.Combine(dir, stem + "_e.png").Replace('\\', '/');
            string emissionPath = SysFile.Exists(ePath) ? ePath : path; // self-reference fallback
            AssignSecondaryTexture(path, emissionPath, "_EmissionMap");
        }

        return count;
    }

    // ── flat normals (fire sprites) ─────────────────────────────────────────
    // Fire doesn't catch directional light — all opaque pixels get (0,0,1)
    // so NdotL is uniform regardless of sun/torch angle.
    static void ProcessFlatNormal(Texture2D source) {
        string srcPath = AssetDatabase.GetAssetPath(source);
        TextureImporter imp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
        if (imp == null) return;

        bool wasReadable = imp.isReadable;
        if (!wasReadable) { imp.isReadable = true; imp.SaveAndReimport(); }

        Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(srcPath);
        if (tex == null) { if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); } return; }
        Color32[] src = tex.GetPixels32();
        int w = tex.width, h = tex.height;
        Color32[] dst = new Color32[w * h];

        for (int i = 0; i < w * h; i++) {
            byte a = (byte)(src[i].a < 128 ? 0 : 255);
            dst[i] = new Color32(128, 128, 255, a);
        }

        if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }

        Texture2D normalTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        normalTex.SetPixels32(dst);
        normalTex.Apply();

        string dir     = SysPath.GetDirectoryName(srcPath);
        string stem    = SysPath.GetFileNameWithoutExtension(srcPath);
        string outPath = SysPath.Combine(dir, stem + "_n.png").Replace('\\', '/');
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

        AssignSecondaryTexture(srcPath, outPath, "_NormalMap");
        Debug.Log($"[NormalMapGen] Written (flat): {outPath}");
    }

    // ── core ─────────────────────────────────────────────────────────────────
    static void ProcessTexture(Texture2D source) {
        string srcPath = AssetDatabase.GetAssetPath(source);
        TextureImporter imp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
        if (imp == null) return;

        // Temporarily enable CPU read access
        bool wasReadable = imp.isReadable;
        if (!wasReadable) { imp.isReadable = true; imp.SaveAndReimport(); }

        // Reload after reimport — the old reference may be stale
        Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(srcPath);
        if (tex == null) { if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); } return; }
        Color32[] src = tex.GetPixels32();
        int w = tex.width, h = tex.height;
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

        foreach (RectInt r in rects) {
            int xMin = r.xMin, xMax = r.xMax, yMin = r.yMin, yMax = r.yMax;
            for (int y = yMin; y < yMax; y++) {
                for (int x = xMin; x < xMax; x++) {
                    int i = y * w + x;
                    if (src[i].a < 128) { dst[i] = new Color32(128, 128, 255, 0); continue; }

                    // Edges are: rect boundary OR a transparent neighbour. Off-rect
                    // pixels are treated as transparent regardless of their actual
                    // alpha, which is what isolates per-slice processing.
                    bool eL = x == xMin       || src[y * w + (x - 1)].a < 128;
                    bool eR = x == xMax - 1   || src[y * w + (x + 1)].a < 128;
                    bool eD = y == yMin       || src[(y - 1) * w + x].a < 128;
                    bool eU = y == yMax - 1   || src[(y + 1) * w + x].a < 128;

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

        if (!wasReadable) { imp.isReadable = false; imp.SaveAndReimport(); }

        // Write output PNG
        Texture2D normalTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        normalTex.SetPixels32(dst);
        normalTex.Apply();

        string dir     = SysPath.GetDirectoryName(srcPath);
        string stem    = SysPath.GetFileNameWithoutExtension(srcPath);
        string outPath = SysPath.Combine(dir, stem + "_n.png").Replace('\\', '/');
        SysFile.WriteAllBytes(outPath, normalTex.EncodeToPNG());
        Object.DestroyImmediate(normalTex);

        // Import as normal map matching source filter mode
        AssetDatabase.ImportAsset(outPath);
        TextureImporter nImp = AssetImporter.GetAtPath(outPath) as TextureImporter;
        if (nImp != null) {
            // Use Default (not NormalMap) so the texture stays as plain RGBA32 —
            // the same packed 0-1 format that TileSpriteCache bakes and NormalsCapture decodes.
            nImp.textureType        = TextureImporterType.Default;
            nImp.textureCompression = TextureImporterCompression.Uncompressed;
            nImp.filterMode         = imp.filterMode;
            nImp.wrapMode           = TextureWrapMode.Clamp;
            nImp.SaveAndReimport();
        }

        // Assign as _NormalMap secondary texture on the source sprite
        AssignSecondaryTexture(srcPath, outPath, "_NormalMap");

        // If a `_e.png` emission mask companion exists, wire it as `_EmissionMap` so
        // EmissionWriter.shader picks it up at lighting time.
        string dir_e  = SysPath.GetDirectoryName(srcPath);
        string stem_e = SysPath.GetFileNameWithoutExtension(srcPath);
        string ePath  = SysPath.Combine(dir_e, stem_e + "_e.png").Replace('\\', '/');
        if (SysFile.Exists(ePath)) {
            AssignSecondaryTexture(srcPath, ePath, "_EmissionMap");
        }

        Debug.Log($"[NormalMapGen] Written: {outPath}");
    }

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

    // ── userData flags ───────────────────────────────────────────────────────
    // Importer userData carries semicolon-separated key=value pairs. Used here
    // for the "merged" flag that tells the generator to treat a multi-sliced
    // texture as one big sprite (spatial-tile sheets, not animation strips).
    static bool HasUserDataFlag(TextureImporter imp, string key, string value) {
        if (imp == null || string.IsNullOrEmpty(imp.userData)) return false;
        foreach (string pair in imp.userData.Split(';')) {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair.Substring(0, eq).Trim() == key && pair.Substring(eq + 1).Trim() == value)
                return true;
        }
        return false;
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

    static void ClearUserDataFlag(TextureImporter imp, string key) {
        if (string.IsNullOrEmpty(imp.userData)) return;
        var pairs = new List<string>();
        foreach (string pair in imp.userData.Split(';')) {
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? pair.Substring(0, eq).Trim() : pair.Trim();
            if (k != key && !string.IsNullOrWhiteSpace(pair)) pairs.Add(pair);
        }
        imp.userData = string.Join(";", pairs);
    }

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
            imp.SaveAndReimport();
            Debug.Log($"[NormalMapGen] Sliced {path} into {rows} rows (merged normals ON). Re-run normal map generation.");
        }
    }
}
