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

        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int i = y * w + x;
                if (src[i].a < 128) { dst[i] = new Color32(128, 128, 255, 0); continue; }

                bool eL = x == 0   || src[y * w + (x - 1)].a < 128;
                bool eR = x == w-1 || src[y * w + (x + 1)].a < 128;
                bool eD = y == 0   || src[(y - 1) * w + x].a < 128;
                bool eU = y == h-1 || src[(y + 1) * w + x].a < 128;

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
}
