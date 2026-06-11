using System.Collections.Generic;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

// ── Gameplay Atlas Builder ─────────────────────────────────────────────
// Single source of truth for SpriteAtlas (V1) setup. All atlases share
// the same packing + platform settings so SRP Batcher sees atlas pages
// as interchangeable — same shader, same material, same compression.
// Filter mode is per render context: world atlases sample Point (pixel-art
// crispness), UI atlases sample Bilinear (the UI scales by a non-integer
// settings factor, so Point shimmers/drops pixels there). Re-runnable:
// pressing the menu item again refreshes the atlas from its source folders.
//
// **Why V1 and not V2:** Unity 2021.3's V2 sprite atlas pipeline is
// incomplete — V2 atlases create the `SpriteAtlasAsset` source but never
// generate the runtime `SpriteAtlas` asset, so sprites don't bind. V1 is
// the supported path in this Unity version. V2 was tried and reverted.
//
// Background: see plans/gpu-perf-pass.md §"Phase 4b — Sprite atlasing".
// Sprite.shader's _MainTex is [PerRendererData], so SRP Batcher keys on
// per-sprite texture. Atlasing forces same-bucket sprites onto a shared
// _MainTex so the batcher can keep state across them.
// AlwaysOnAtlas packer mode is enforced on editor load so the V1 atlases below
// actually generate runtime data. Used to be a one-shot menu item; promoted to
// auto-enforcement so it can never drift back to V2.
[InitializeOnLoad]
public static class GameplayAtlasBuilder
{
    const string AtlasFolder = "Assets/SpriteAtlases";

    static GameplayAtlasBuilder()
    {
        if (EditorSettings.spritePackerMode != SpritePackerMode.AlwaysOnAtlas)
            EditorSettings.spritePackerMode = SpritePackerMode.AlwaysOnAtlas;
    }

    // ── Menu items ──────────────────────────────────────────────────────

    // Single entry point: rebuilds every gameplay atlas in one pass. Individual
    // per-atlas menus were collapsed into this — rebuilds are cheap (~seconds
    // total) and a partial atlas state is almost never useful.
    [MenuItem("Tools/Rebuild Atlases", priority = 1)]
    public static void RebuildAll()
    {
        // Misc/ splits by render context. cloud/rain/background stay un-atlased
        // entirely — custom shader/MPB wiring outside the standard sprite
        // pipeline; atlasing would change their underlying texture and silently
        // break the renderer.
        var customShaderPrefixes = new[] { "cloud", "rain", "background" };
        System.Func<string, bool> isCustomShaderSprite = name =>
        {
            var lower = name.ToLowerInvariant();
            foreach (var prefix in customShaderPrefixes)
                if (lower.StartsWith(prefix)) return true;
            return false;
        };

        // World-rendered Misc sprites (SpriteRenderer overlays / material
        // textures: selection + blueprint frames, cursor, structure cracks).
        // Kept in their own Point atlas so the pixel-art world stays crisp
        // while the rest of Misc/ goes Bilinear with the UI. Unreferenced
        // strays (mycelium, tree, selectframe) default here — Point is the
        // safe side for anything that might end up world-rendered.
        var worldOverlayNames = new HashSet<string>
        {
            "mousearrow", "whiteborder", "tileselect", "harvestselect",
            "blueprintframe", "bpdeconstructframe", "crack",
            "mycelium", "tree", "selectframe",
        };
        System.Func<string, bool> isWorldOverlay = name =>
            worldOverlayNames.Contains(System.IO.Path.GetFileNameWithoutExtension(name).ToLowerInvariant());

        // Atlas roster. Folder notes:
        // - Animals is recursive (picks up Clothing/); Buildings picks up
        //   furnishings/. Plants skips Plants/Sheets (editor input only — split
        //   into Plants/Split at edit time by PlantSheetSplitter).
        // - FloorItems vs ItemIcons split: Items/split/<item>/{icon,floor,qhigh,
        //   qmid,qlow,shigh,smid,slow}.png. icon.png is UI-only — owned by
        //   ItemIcons. Other slots are gameplay-rendered (floor piles, stacks).
        // - UIChrome: buttons, frames, scrollbars, dividers, status indicators.
        //   Packs Misc/ recursively (covers buildicons/ + indicator/ subfolders).
        var builds = new (string name, Object[] packables, FilterMode filter)[]
        {
            ("Animals",   LoadSpritesUnder(new[] { "Assets/Resources/Sprites/Animals" }), FilterMode.Point),
            ("Buildings", LoadSpritesUnder(new[] { "Assets/Resources/Sprites/Buildings" }), FilterMode.Point),
            ("Plants",    LoadSpritesUnder(new[] {
                "Assets/Resources/Sprites/Plants/Decorative",
                "Assets/Resources/Sprites/Plants/Split",
            }), FilterMode.Point),
            ("FloorItems", LoadSpritesUnderFiltered(
                "Assets/Resources/Sprites/Items/split", excludeFileName: "icon.png"), FilterMode.Point),
            ("ItemIcons", LoadSpritesUnderMatching(
                "Assets/Resources/Sprites/Items/split", includeFileName: "icon.png"), FilterMode.Bilinear),
            ("UIChrome", LoadSpritesUnder(new[] { "Assets/Resources/Sprites/Misc" },
                name => !isCustomShaderSprite(name) && !isWorldOverlay(name)), FilterMode.Bilinear),
            ("WorldOverlays", LoadSpritesUnder(new[] { "Assets/Resources/Sprites/Misc" },
                name => !isCustomShaderSprite(name) && isWorldOverlay(name)), FilterMode.Point),
        };

        // All importer syncs run BEFORE any packing: SaveAndReimport invalidates
        // already-packed atlas data, so a sync interleaved between builds would
        // silently unpack earlier atlases (spriteCount drops to 0 with no error).
        foreach (var b in builds)
            SyncImporterFilter(b.packables, b.filter);

        // Researches/ + Skills/ icons are UI-only but never atlased (loaded ad
        // hoc by ResearchDisplay/SkillDisplay), so their importer setting is the
        // only filter control — sync it here alongside the atlas rebuild.
        SyncImporterFilter(LoadSpritesUnder(new[] {
            "Assets/Resources/Sprites/Researches",
            "Assets/Resources/Sprites/Skills",
        }), FilterMode.Bilinear);

        var created = new List<SpriteAtlas>();
        foreach (var b in builds)
            created.Add(CreateAtlasAsset(b.name, b.packables, b.filter));

        // Packing is the LAST operation: SaveAssets/Refresh after a pack
        // silently invalidates the packed data (spriteCount drops to 0, no
        // error; AlwaysOnAtlas would re-pack on Play, but the builder's own
        // logs and any post-build spriteCount checks would lie meanwhile).
        Refresh();
        SpriteAtlasUtility.PackAtlases(created.ToArray(), EditorUserBuildSettings.activeBuildTarget);

        for (int i = 0; i < created.Count; i++)
        {
            var atlas = created[i];
            if (atlas.spriteCount == 0)
                Debug.LogError($"[GameplayAtlasBuilder] {atlas.name} packed 0 sprites ({builds[i].packables.Length} packables) — atlas is not usable.");
            else
                Debug.Log($"[GameplayAtlasBuilder] Built {atlas.name} ({builds[i].packables.Length} packables, atlas.spriteCount={atlas.spriteCount}, filter={builds[i].filter}).");
        }
    }

    // ── Core ────────────────────────────────────────────────────────────

    // V1 atlas creation: instantiate SpriteAtlas, configure via the
    // SpriteAtlasExtensions API, save via CreateAsset. Packing happens once
    // for all atlases at the end of RebuildAll — see the comment there.
    static SpriteAtlas CreateAtlasAsset(string name, Object[] packables, FilterMode filter)
    {
        string path = $"{AtlasFolder}/{name}.spriteatlas";
        if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        var textureSettings = StandardTexture;
        textureSettings.filterMode = filter;

        var atlas = new SpriteAtlas();
        atlas.name = name;
        atlas.SetPackingSettings(StandardPacking);
        atlas.SetTextureSettings(textureSettings);
        atlas.SetPlatformSettings(StandardPlatform);
        atlas.Add(packables);

        AssetDatabase.CreateAsset(atlas, path);
        AssetDatabase.SaveAssets();
        return atlas;
    }

    // Keep source importers' filterMode matching their atlas so editor previews
    // and any unpacked fallback path sample the same way the atlas does. No-op
    // (no reimport churn) for textures already matching. Must run before any
    // packing — see the call site in RebuildAll.
    static void SyncImporterFilter(Object[] packables, FilterMode filter)
    {
        foreach (var obj in packables)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"[GameplayAtlasBuilder] No TextureImporter for {path} — filter mode not synced.");
                continue;
            }
            if (importer.filterMode == filter) continue;
            importer.filterMode = filter;
            importer.SaveAndReimport();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    // Per-file packables: enumerate every sprite-imported PNG under `root`
    // (recursive), skip files matching `excludeFileName`, return parent
    // Texture2Ds so any sliced sub-sprites get picked up.
    static Object[] LoadSpritesUnderFiltered(string root, string excludeFileName)
    {
        return LoadSpritesUnder(new[] { root }, name =>
            !string.Equals(name, excludeFileName, System.StringComparison.OrdinalIgnoreCase));
    }

    // Per-file packables: only include sprites whose filename matches.
    static Object[] LoadSpritesUnderMatching(string root, string includeFileName)
    {
        return LoadSpritesUnder(new[] { root }, name =>
            string.Equals(name, includeFileName, System.StringComparison.OrdinalIgnoreCase));
    }

    // Aseprite (.ase / .aseprite) source files are editing input only — they
    // generate sub-sprites that shadow the canonical .png exports. Filter at
    // the atlas-build boundary so they never reach a runtime atlas.
    // The complementary runtime guard is AsepriteResourcesGuard, which moves
    // .ase files out of Resources/ on import.
    static Object[] LoadSpritesUnder(string[] roots, System.Func<string, bool> predicate = null)
    {
        var seenPaths = new HashSet<string>();
        var list = new List<Object>();
        foreach (var g in AssetDatabase.FindAssets("t:Sprite", roots))
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            if (!seenPaths.Add(path)) continue;
            if (path.EndsWith(".ase", System.StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".aseprite", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (predicate != null && !predicate(System.IO.Path.GetFileName(path))) continue;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex != null) list.Add(tex);
        }
        return list.ToArray();
    }

    static void Refresh()
    {
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // ── Shared atlas settings ───────────────────────────────────────────
    // Uncompressed to preserve pixel-art fidelity; filter mode is supplied
    // per atlas (StandardTexture's value is a placeholder BuildAtlas always
    // overwrites). Padding 2 matches earlier convention. Max 4096 gives
    // comfortable headroom so we don't silently spill into multiple pages
    // (which would re-fragment the batching win).

    static readonly SpriteAtlasPackingSettings StandardPacking = new SpriteAtlasPackingSettings
    {
        blockOffset         = 1,
        enableRotation      = false,
        enableTightPacking  = false,
        enableAlphaDilation = false,
        padding             = 2,
    };

    static readonly SpriteAtlasTextureSettings StandardTexture = new SpriteAtlasTextureSettings
    {
        anisoLevel      = 0,
        filterMode      = FilterMode.Point,
        generateMipMaps = false,
        readable        = false,
        sRGB            = true,
    };

    static readonly TextureImporterPlatformSettings StandardPlatform = new TextureImporterPlatformSettings
    {
        name                = "DefaultTexturePlatform",
        maxTextureSize      = 4096,
        textureCompression  = TextureImporterCompression.Uncompressed,
        crunchedCompression = false,
        overridden          = false,
    };
}
