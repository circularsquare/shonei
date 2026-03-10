using UnityEngine;
using UnityEditor;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;

// Right-click any Texture2D in the Project window and choose
// "Generate Sprite Normal Map" to produce a _n.png normal map beside it.
//
// Algorithm:
//   - Transparent pixels  → neutral normal (0.5, 0.5, 1.0)
//   - Interior pixels     → neutral normal (faces camera)
//   - Edge pixels         → normal pointing outward in XY, blended with Z=BevelZ
//   - Corner pixels       → diagonal outward normal (same logic, two XY components)
//
// BevelZ controls how steep the edge tilt is:
//   higher = shallower bevel (more frontal catch), 1 = 45° bevel.
public static class SpriteNormalMapGenerator {
    const float BevelZ = 1f;

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

    static void ProcessTexture(Texture2D source) {
        string srcPath = AssetDatabase.GetAssetPath(source);
        TextureImporter imp = AssetImporter.GetAtPath(srcPath) as TextureImporter;
        if (imp == null) return;

        // Temporarily enable CPU read access
        bool wasReadable = imp.isReadable;
        if (!wasReadable) { imp.isReadable = true; imp.SaveAndReimport(); }

        Color32[] src = source.GetPixels32();
        int w = source.width, h = source.height;
        Color32[] dst = new Color32[w * h];

        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                int i = y * w + x;
                if (src[i].a < 128) { dst[i] = new Color32(128, 128, 255, 0); continue; }

                // A pixel is an "edge" if any of its 4 neighbours is transparent (or out-of-bounds).
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

        string dir    = SysPath.GetDirectoryName(srcPath);
        string stem   = SysPath.GetFileNameWithoutExtension(srcPath);
        string outPath = SysPath.Combine(dir, stem + "_n.png").Replace('\\', '/');
        SysFile.WriteAllBytes(outPath, normalTex.EncodeToPNG());
        Object.DestroyImmediate(normalTex);

        // Import as normal map, matching source filter mode
        AssetDatabase.ImportAsset(outPath);
        TextureImporter nImp = AssetImporter.GetAtPath(outPath) as TextureImporter;
        if (nImp != null) {
            nImp.textureType = TextureImporterType.NormalMap;
            nImp.filterMode  = imp.filterMode;
            nImp.SaveAndReimport();
        }

        Debug.Log($"[NormalMapGen] Written: {outPath}");
    }
}
