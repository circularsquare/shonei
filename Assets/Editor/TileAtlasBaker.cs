using System;
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
// Run once after pulling a fresh branch ("Tools → Bake Tile Edges").
// "Bake Tile Edges" skips any type/overlay whose baked .asset is newer than its
// newest source PNG — re-running it after no source changes is a near-instant no-op.
// Use "Bake Tile Edges (Force)" to ignore timestamps and rebake everything.
// After that, TileAtlasBakeOnImport (AssetPostprocessor) re-runs the relevant
// per-name bake whenever an artist saves over a source PNG under
// `Resources/Sprites/Tiles/` — no manual step needed.
//
// Output assets are committed to git so first-time clones / CI builds don't
// re-bake from scratch.
public static class TileAtlasBaker {
    const string BakedDir         = "Assets/Resources/BakedTileAtlases";
    const string BakedResourceDir = "BakedTileAtlases"; // for Resources.Load
    const string SheetsDir        = "Assets/Resources/Sprites/Tiles/Sheets";
    const string FlatDir          = "Assets/Resources/Sprites/Tiles";
    const string SnowOverlayName  = "snow"; // hardcoded by TileMeshController
    // Background-wall atlases — NOT tile types (not in Db.tileTypes), so the body
    // bake enumerates them explicitly. Must match BackgroundTileMeshController.Registry.
    // Body-only consumers (the flat-lit background renderer never binds _normal), but
    // BakeType writes both {name}_body + {name}_normal harmlessly.
    static readonly string[] BackgroundAtlasNames = { "stoneback", "dirtback" };

    // ── Entry points ────────────────────────────────────────────────────

    [MenuItem("Tools/Bake Tile Edges", priority = 140)]
    public static void BakeAll() {
        BakeAllImpl(force: false);
    }

    // Force-rebake every type and overlay regardless of source/asset timestamps.
    // Use after deleting a variant PNG (so no surviving source got touched), or
    // if a baked .asset was manually edited and its mtime no longer reflects
    // source-derived state.
    [MenuItem("Tools/Bake Tile Edges (Force)", priority = 141)]
    public static void BakeAllForce() {
        BakeAllImpl(force: true);
    }

    static void BakeAllImpl(bool force) {
        EnsureDirectory();
        EnsureDbLoaded();

        var sw = Stopwatch.StartNew();
        int bodyBaked = 0, bodySkipped = 0, overlayBaked = 0, overlaySkipped = 0;

        foreach (var tt in Db.tileTypes) {
            if (tt == null || !tt.solid) continue;
            if (!force && IsBodyUpToDate(tt.name)) { bodySkipped++; continue; }
            BakeType(tt.name);
            bodyBaked++;
        }

        // Background-wall body atlases (skip names whose source art the artist hasn't
        // drawn yet — a missing stem would otherwise bake a magenta fallback asset).
        foreach (var name in BackgroundAtlasNames) {
            if (Resources.Load<Texture2D>($"Sprites/Tiles/Sheets/{name}") == null
                && Resources.Load<Texture2D>($"Sprites/Tiles/{name}") == null) continue;
            if (!force && IsBodyUpToDate(name)) { bodySkipped++; continue; }
            BakeType(name);
            bodyBaked++;
        }

        foreach (var name in CollectOverlayNames()) {
            if (!force && IsOverlayUpToDate(name)) { overlaySkipped++; continue; }
            BakeOverlay(name);
            overlayBaked++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[TileAtlasBaker] Bodies: {bodyBaked} baked, {bodySkipped} skipped (up-to-date). " +
                  $"Overlays: {overlayBaked} baked, {overlaySkipped} skipped. " +
                  $"Total {sw.ElapsedMilliseconds} ms{(force ? " (force)" : "")}");
    }

    // ── Up-to-date checks ───────────────────────────────────────────────
    // Compare the baked .asset's mtime to the newest source PNG's mtime. If the
    // asset is newer (and complete), nothing under that name has changed since
    // the last bake.

    static bool IsBodyUpToDate(string name) {
        string bodyPath   = $"{BakedDir}/{name}_body.asset";
        string normalPath = $"{BakedDir}/{name}_normal.asset";
        if (!File.Exists(bodyPath) || !File.Exists(normalPath)) return false;
        var newestSource = NewestSourceMtime(name);
        if (newestSource == DateTime.MinValue) return false; // no sources: let BakeType handle / log
        var bakedMtime = MinMtime(bodyPath, normalPath);
        return bakedMtime > newestSource;
    }

    static bool IsOverlayUpToDate(string name) {
        string overlayPath = $"{BakedDir}/{name}_overlay.asset";
        if (!File.Exists(overlayPath)) {
            // Asset missing — but if no source exists either, the overlay is
            // legitimately absent (e.g. grass_dying not authored). Treat as
            // up-to-date so BakeOverlay's no-source early-return doesn't run
            // pointlessly each time.
            return NewestSourceMtime(name) == DateTime.MinValue;
        }
        var newestSource = NewestSourceMtime(name);
        if (newestSource == DateTime.MinValue) return true; // asset exists but source gone — let force handle it
        return File.GetLastWriteTimeUtc(overlayPath) > newestSource;
    }

    // Newest mtime across every variant PNG that contributes to atlas `name`
    // — atlas variants `Sheets/<name>{,2,3,…}.png` and flat fallbacks
    // `Tiles/<name>{,2,3,…}.png`. Returns DateTime.MinValue if nothing exists.
    static DateTime NewestSourceMtime(string name) {
        var newest = DateTime.MinValue;
        ScanVariantSeries(SheetsDir, name, ref newest);
        ScanVariantSeries(FlatDir,   name, ref newest);
        return newest;
    }

    static void ScanVariantSeries(string dir, string name, ref DateTime newest) {
        for (int i = 1; ; i++) {
            string suffix = i == 1 ? "" : i.ToString();
            string path = $"{dir}/{name}{suffix}.png";
            if (!File.Exists(path)) break;
            var t = File.GetLastWriteTimeUtc(path);
            if (t > newest) newest = t;
        }
    }

    static DateTime MinMtime(string a, string b) {
        var ta = File.GetLastWriteTimeUtc(a);
        var tb = File.GetLastWriteTimeUtc(b);
        return ta < tb ? ta : tb;
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
        var existing = UnityEngine.Object.FindObjectOfType<Db>(includeInactive: true);
        if (existing != null) {
            existing.LoadAll();
        } else {
            var go = new GameObject("__TempDbForBake__");
            var db = go.AddComponent<Db>();
            db.LoadAll();
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // CreateAsset throws if the path already exists — overwrite by deleting
    // first. The pre-bake DeleteAsset is what guarantees the new Texture2DArray
    // fully replaces the old one (slice count, format, name).
    static void WriteAsset(UnityEngine.Object obj, string path) {
        if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(obj, path);
    }
}
