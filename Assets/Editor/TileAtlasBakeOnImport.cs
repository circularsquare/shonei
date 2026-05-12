using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Watches for changes to source tile PNGs under `Assets/Resources/Sprites/Tiles/`
// and re-bakes the affected output atlases. The artist workflow stays "edit PNG,
// save, see result on next Play press" — no manual menu step.
//
// Imports, deletions, and renames all trigger a re-bake (a deleted variant
// reduces N_variants, so the output needs to shrink to match). Multiple PNGs
// affecting the same atlas-name are coalesced into a single bake.
//
// The output `.asset` files live under `Assets/Resources/BakedTileAtlases/`,
// which is OUTSIDE the watched directories, so the recursion case
// (postprocessor fires for its own outputs) naturally doesn't match the
// source-path filters below.
public class TileAtlasBakeOnImport : AssetPostprocessor {
    const string SheetsDir = "Assets/Resources/Sprites/Tiles/Sheets/";
    const string FlatDir   = "Assets/Resources/Sprites/Tiles/";

    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) {
        var bodies   = new HashSet<string>();
        var overlays = new HashSet<string>();
        Scan(imported,  bodies, overlays);
        Scan(deleted,   bodies, overlays);
        Scan(moved,     bodies, overlays);
        Scan(movedFrom, bodies, overlays);

        if (bodies.Count == 0 && overlays.Count == 0) return;

        foreach (var name in bodies)   TileAtlasBaker.BakeType(name);
        foreach (var name in overlays) TileAtlasBaker.BakeOverlay(name);
        AssetDatabase.SaveAssets();
    }

    static void Scan(string[] paths, HashSet<string> bodies, HashSet<string> overlays) {
        if (paths == null) return;
        foreach (var p in paths) {
            if (!p.EndsWith(".png")) continue;
            // Normalize separators — paths come back with forward slashes on
            // both platforms, but be defensive.
            string norm = p.Replace('\\', '/');
            string dir  = norm.Substring(0, norm.LastIndexOf('/') + 1);
            // Match SHEETS first — its prefix also matches FLAT (Sheets is a
            // subfolder), so order matters.
            if (dir != SheetsDir && dir != FlatDir) continue;

            string stem = System.IO.Path.GetFileNameWithoutExtension(norm);
            string name = TileAtlasBaker.StripVariantSuffix(stem);
            if (TileAtlasBaker.IsBodyName(name))         bodies.Add(name);
            else if (TileAtlasBaker.IsOverlayName(name)) overlays.Add(name);
            // Names matching neither (e.g. an unrelated tile sprite or a
            // typo) are silently ignored — no risk of producing orphan assets.
        }
    }
}
