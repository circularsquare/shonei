using System.Collections.Generic;
using System.Text;
using UnityEditor;

// Shared helpers used by sheet splitters and the normal-map generator.
// Importer userData carries semicolon-separated key=value pairs; the cache-key
// stamps (`itemSplitCacheKey`, `plantSplitCacheKey`, `normalsCacheKey`) all coexist
// on the same sheet's userData.
//
// Pulled out of duplicate copies in ItemSheetSplitter, PlantSheetSplitter, and
// SpriteNormalMapGenerator. Use `using static EditorUtilities;` to call unqualified.
public static class EditorUtilities {
    public static string GetUserDataValue(TextureImporter imp, string key) {
        if (imp == null || string.IsNullOrEmpty(imp.userData)) return null;
        foreach (string pair in imp.userData.Split(';')) {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair.Substring(0, eq).Trim() == key) return pair.Substring(eq + 1).Trim();
        }
        return null;
    }

    public static bool HasUserDataFlag(TextureImporter imp, string key, string value) {
        if (imp == null || string.IsNullOrEmpty(imp.userData)) return false;
        foreach (string pair in imp.userData.Split(';')) {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair.Substring(0, eq).Trim() == key && pair.Substring(eq + 1).Trim() == value)
                return true;
        }
        return false;
    }

    public static void SetUserDataFlag(TextureImporter imp, string key, string value) {
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

    public static void ClearUserDataFlag(TextureImporter imp, string key) {
        if (imp == null || string.IsNullOrEmpty(imp.userData)) return;
        var pairs = new List<string>();
        foreach (string pair in imp.userData.Split(';')) {
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? pair.Substring(0, eq).Trim() : pair.Trim();
            if (k == key) continue;
            if (!string.IsNullOrWhiteSpace(pair)) pairs.Add(pair);
        }
        imp.userData = string.Join(";", pairs);
    }

    public static string Md5(string s) {
        using (var m = System.Security.Cryptography.MD5.Create()) {
            byte[] b = m.ComputeHash(Encoding.UTF8.GetBytes(s));
            return System.Convert.ToBase64String(b);
        }
    }
}
