using System.Collections.Generic;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

// ── Gameplay Atlas Builder ─────────────────────────────────────────────
// Single source of truth for SpriteAtlas (V2) setup. All atlases share
// the same packing + platform settings so SRP Batcher sees atlas pages
// as interchangeable — same shader, same material, same compression.
// Filter mode is per render context: world atlases sample Point (pixel-art
// crispness), UI atlases sample Bilinear (the UI scales by a non-integer
// settings factor, so Point shimmers/drops pixels there). Re-runnable:
// pressing the menu item again refreshes the atlas from its source folders.
//
// **V2, not V1.** The Unity 6 move silently broke V1 `AlwaysOnAtlas` packing —
// sprites stopped binding to atlas pages at runtime (sprite.packed==false
// everywhere, rendering as if un-atlased). V2 is the supported path in U6.
// Authoring differs from V1: build a `SpriteAtlasAsset` source (Add packables
// + `SpriteAtlasAsset.Save` to `.spriteatlasv2`), then apply packing/texture/
// platform settings on the imported `SpriteAtlasImporter` — the matching
// setters on `SpriteAtlasAsset` itself are [Obsolete]. V2 packs on entering
// Play mode / build (not at edit-rest), so verify with sprite.packed in Play.
//
// Background: see plans/sprite-atlas-v2-migration.md and gpu-perf-pass.md
// §"Phase 4b — Sprite atlasing". Sprite.shader's _MainTex is [PerRendererData],
// so SRP Batcher keys on per-sprite texture. Atlasing forces same-bucket
// sprites onto a shared _MainTex so the batcher can keep state across them.
// SpriteAtlasV2 packer mode is enforced on editor load.
[InitializeOnLoad]
public static class GameplayAtlasBuilder
{
    const string AtlasFolder = "Assets/SpriteAtlases";

    static GameplayAtlasBuilder()
    {
        if (EditorSettings.spritePackerMode != SpritePackerMode.SpriteAtlasV2)
            EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;
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
            "mycelium", "tree", "selectframe", "questionmark",
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

        Refresh();

        // Force an editor-side pack so the spriteCount log below is meaningful.
        // V2 also packs automatically on entering Play mode / build; this just
        // surfaces problems now instead of waiting for Play.
        var packable = created.FindAll(a => a != null);
        if (packable.Count > 0)
            SpriteAtlasUtility.PackAtlases(packable.ToArray(), EditorUserBuildSettings.activeBuildTarget);

        for (int i = 0; i < created.Count; i++)
        {
            var atlas = created[i];
            int packCount = builds[i].packables.Length;
            if (atlas == null)
                Debug.LogError($"[GameplayAtlasBuilder] {builds[i].name} failed to import ({packCount} packables).");
            else if (atlas.spriteCount == 0)
                Debug.LogWarning($"[GameplayAtlasBuilder] {builds[i].name}: {packCount} packables added but spriteCount=0 after pack — verify in Play mode (V2 packs lazily). If still 0 in Play, atlas is broken.");
            else
                Debug.Log($"[GameplayAtlasBuilder] Built {builds[i].name} ({packCount} packables, spriteCount={atlas.spriteCount}, filter={builds[i].filter}).");
        }
    }

    // ── Core ────────────────────────────────────────────────────────────

    // V2 atlas creation: author a SpriteAtlasAsset source (Add + Save to
    // .spriteatlasv2), import it, then apply packing/texture/platform settings
    // on the resulting SpriteAtlasImporter — the matching SpriteAtlasAsset
    // setters are [Obsolete] under V2. Unlike V1 there's no shared global pack
    // state to invalidate, so configuring each atlas fully in sequence is safe.
    // Returns the imported runtime SpriteAtlas (may report spriteCount 0 until
    // packed on Play/build — expected for V2; see RebuildAll's log handling).
    static SpriteAtlas CreateAtlasAsset(string name, Object[] packables, FilterMode filter)
    {
        string path = $"{AtlasFolder}/{name}.spriteatlasv2";
        if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        // Author + save the V2 source asset, then import it.
        var asset = new SpriteAtlasAsset();
        asset.Add(packables);
        SpriteAtlasAsset.Save(asset, path);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        // V2 settings live on the importer, not the asset.
        var importer = AssetImporter.GetAtPath(path) as SpriteAtlasImporter;
        if (importer == null)
        {
            Debug.LogError($"[GameplayAtlasBuilder] No SpriteAtlasImporter for {path} — atlas not configured.");
            return null;
        }

        var textureSettings = StandardTexture;
        textureSettings.filterMode = filter;
        importer.packingSettings = StandardPacking;
        importer.textureSettings = textureSettings;
        importer.SetPlatformSettings(StandardPlatform);
        importer.includeInBuild = true;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
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
