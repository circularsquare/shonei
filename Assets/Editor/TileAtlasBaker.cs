using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Offline tile atlas baker. Precomputes the Texture2DArrays that TileSpriteCache
// would otherwise build at runtime (~5 seconds of normal-map distance-transform
// math on first chunk rebuild) and writes them as .asset files under
// `Assets/Resources/BakedTileAtlases/`. The runtime EnsureTypeArrayBundle /
// EnsureOverlayArrayBundle fast-paths load these via `Resources.Load` so play
// mode skips the bake entirely.
//
// Run once after pulling a fresh branch ("Tools → Bake All Tile Atlases").
// After that, TileAtlasBakeOnImport (AssetPostprocessor) re-runs the relevant
// per-name bake whenever an artist saves over a source PNG under
// `Resources/Sprites/Tiles/` — no manual step needed.
//
// Output assets are committed to git so first-time clones / CI builds don't
// re-bake from scratch.
public static class TileAtlasBaker {
    const string BakedDir         = "Assets/Resources/BakedTileAtlases";
    const string BakedResourceDir = "BakedTileAtlases"; // for Resources.Load
    const string SnowOverlayName  = "snow"; // hardcoded by TileMeshController

    // ── Entry points ────────────────────────────────────────────────────

    [MenuItem("Tools/Bake All Tile Atlases")]
    public static void BakeAll() {
        EnsureDirectory();
        EnsureDbLoaded();

        var sw = Stopwatch.StartNew();
        int bodyCount = 0, overlayCount = 0;

        foreach (var tt in Db.tileTypes) {
            if (tt == null || !tt.solid) continue;
            BakeType(tt.name);
            bodyCount++;
        }

        foreach (var name in CollectOverlayNames()) {
            BakeOverlay(name);
            overlayCount++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[TileAtlasBaker] Baked {bodyCount} body types + {overlayCount} overlays in {sw.ElapsedMilliseconds} ms");
    }

    // Single-type bake — called by the postprocessor when a source PNG is saved.
    public static void BakeType(string typeName) {
        EnsureDirectory();
        EnsureDbLoaded();
        var sw = Stopwatch.StartNew();

        var (body, normal) = TileSpriteCache.BakeTypeArraysForEditor(typeName);
        body.name   = $"{typeName}_body";
        normal.name = $"{typeName}_normal";

        WriteAsset(body,   $"{BakedDir}/{typeName}_body.asset");
        WriteAsset(normal, $"{BakedDir}/{typeName}_normal.asset");
        Debug.Log($"[TileAtlasBaker] Rebaked body+normal for '{typeName}' ({sw.ElapsedMilliseconds} ms)");
    }

    public static void BakeOverlay(string overlayName) {
        EnsureDirectory();
        EnsureDbLoaded();
        // Only bake if the source atlas / flat sprite exists — overlay states
        // (e.g. "grass_dying") may be missing for a given base name.
        if (Resources.Load<Texture2D>($"Sprites/Tiles/Sheets/{overlayName}") == null
            && Resources.Load<Texture2D>($"Sprites/Tiles/{overlayName}") == null) {
            return;
        }

        var sw = Stopwatch.StartNew();
        var arr = TileSpriteCache.BakeOverlayArrayForEditor(overlayName);
        arr.name = $"{overlayName}_overlay";
        WriteAsset(arr, $"{BakedDir}/{overlayName}_overlay.asset");
        Debug.Log($"[TileAtlasBaker] Rebaked overlay '{overlayName}' ({sw.ElapsedMilliseconds} ms)");
    }

    // ── Source-name → bake target mapping (used by the postprocessor) ─────

    // Strips the trailing variant-number from a source-PNG filename, returning
    // the atlas name TileSpriteCache uses. e.g. "dirt2" → "dirt", "snow" → "snow".
    public static string StripVariantSuffix(string fileStem) {
        int i = fileStem.Length;
        while (i > 0 && char.IsDigit(fileStem[i - 1])) i--;
        return fileStem.Substring(0, i);
    }

    // True when a given atlas name matches a solid tile-type body. Resolved
    // against Db.tileTypes (must be loaded).
    public static bool IsBodyName(string name) {
        EnsureDbLoaded();
        foreach (var tt in Db.tileTypes) {
            if (tt != null && tt.solid && tt.name == name) return true;
        }
        return false;
    }

    // True when a given atlas name corresponds to an overlay slot (snow,
    // grass, grass_dying, grass_dead, etc.).
    public static bool IsOverlayName(string name) {
        if (name == SnowOverlayName) return true;
        foreach (var oname in CollectOverlayNames()) {
            if (oname == name) return true;
        }
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    static HashSet<string> CollectOverlayNames() {
        EnsureDbLoaded();
        var overlays = new HashSet<string> { SnowOverlayName };
        foreach (var tt in Db.tileTypes) {
            if (tt?.overlay == null) continue;
            overlays.Add(tt.overlay);
            overlays.Add(tt.overlay + "_dying");
            overlays.Add(tt.overlay + "_dead");
        }
        return overlays;
    }

    static void EnsureDirectory() {
        if (!Directory.Exists(BakedDir)) {
            Directory.CreateDirectory(BakedDir);
            AssetDatabase.Refresh();
        }
    }

    // Db.Awake doesn't fire in edit mode, so we explicitly invoke its loader.
    // Prefer the existing scene Db component if present (avoids the
    // "tried to create two instances" guard); otherwise spawn a temp GO so the
    // statics get populated. Idempotent — early-out if already loaded.
    static void EnsureDbLoaded() {
        if (Db.tileTypeByName != null && Db.tileTypeByName.Count > 0) return;
        var existing = Object.FindObjectOfType<Db>(includeInactive: true);
        if (existing != null) {
            existing.LoadAll();
        } else {
            var go = new GameObject("__TempDbForBake__");
            var db = go.AddComponent<Db>();
            db.LoadAll();
            Object.DestroyImmediate(go);
        }
    }

    // CreateAsset throws if the path already exists — overwrite by deleting
    // first. The pre-bake DeleteAsset is what guarantees the new Texture2DArray
    // fully replaces the old one (slice count, format, name).
    static void WriteAsset(Object obj, string path) {
        if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(obj, path);
    }
}
