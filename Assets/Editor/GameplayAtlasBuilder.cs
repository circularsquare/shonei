using System.Collections.Generic;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

// ── Gameplay Atlas Builder ─────────────────────────────────────────────
// Single source of truth for SpriteAtlas (V1) setup. All atlases share
// the same packing + texture + platform settings so SRP Batcher sees
// atlas pages as interchangeable — same shader, same material, same
// filter mode, same compression. Re-runnable: pressing the menu item
// again refreshes the atlas from its source folders.
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
    [MenuItem("Tools/Rebuild Atlases")]
    public static void RebuildAll()
    {
        // Recursive — Animals picks up Clothing/, Buildings picks up furnishings/.
        // Plants skips Plants/Sheets (editor input only — split into Plants/Split
        // at edit time by PlantSheetSplitter).
        BuildAtlas("Animals",   LoadSpritesUnder(new[] { "Assets/Resources/Sprites/Animals" }));
        BuildAtlas("Buildings", LoadSpritesUnder(new[] { "Assets/Resources/Sprites/Buildings" }));
        BuildAtlas("Plants",    LoadSpritesUnder(new[] {
            "Assets/Resources/Sprites/Plants/Decorative",
            "Assets/Resources/Sprites/Plants/Split",
        }));

        // FloorItems vs ItemIcons split: Items/split/<item>/{icon,floor,qhigh,qmid,
        // qlow,shigh,smid,slow}.png. icon.png is UI-only — owned by ItemIcons.
        // Other slots are gameplay-rendered (floor piles, stack visuals).
        BuildAtlas("FloorItems", LoadSpritesUnderFiltered(
            "Assets/Resources/Sprites/Items/split", excludeFileName: "icon.png"));
        BuildAtlas("ItemIcons", LoadSpritesUnderMatching(
            "Assets/Resources/Sprites/Items/split", includeFileName: "icon.png"));

        // UIChrome: buttons, frames, scrollbars, dividers, status indicators.
        // Packs Misc/ recursively (covers buildicons/ + indicator/ subfolders)
        // but excludes known world-render sprites that have custom shader/MPB
        // wiring outside the standard sprite pipeline — atlasing those would
        // change their underlying texture and silently break the renderer.
        var worldSpritePrefixes = new[] { "cloud", "rain", "background" };
        BuildAtlas("UIChrome", LoadSpritesUnder(new[] { "Assets/Resources/Sprites/Misc" }, name =>
        {
            var lower = name.ToLowerInvariant();
            foreach (var prefix in worldSpritePrefixes)
                if (lower.StartsWith(prefix)) return false;
            return true;
        }));

        Refresh();
    }

    // ── Core ────────────────────────────────────────────────────────────

    // V1 atlas creation: instantiate SpriteAtlas, configure via the
    // SpriteAtlasExtensions API, save via CreateAsset, then pack.
    static void BuildAtlas(string name, Object[] packables)
    {
        string path = $"{AtlasFolder}/{name}.spriteatlas";
        if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        var atlas = new SpriteAtlas();
        atlas.name = name;
        atlas.SetPackingSettings(StandardPacking);
        atlas.SetTextureSettings(StandardTexture);
        atlas.SetPlatformSettings(StandardPlatform);
        atlas.Add(packables);

        AssetDatabase.CreateAsset(atlas, path);
        AssetDatabase.SaveAssets();

        // Pack immediately so sprite.packed becomes true at runtime.
        SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget);

        Debug.Log($"[GameplayAtlasBuilder] Built {name} ({packables.Length} packables, atlas.spriteCount={atlas.spriteCount}).");
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
    // Point filter + uncompressed to preserve pixel-art crispness. Padding 2
    // matches earlier convention. Max 4096 gives comfortable headroom so we
    // don't silently spill into multiple pages (which would re-fragment the
    // batching win).

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
