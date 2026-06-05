using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

// ── Building Edge Mask Baker ───────────────────────────────────────────
// Bakes, per building, two bitmasks of which footprint tiles present a solid
// wall on their LEFT / RIGHT edge — read at runtime by StructType.SideEdgeSolid
// so a side-ladder can't mount against a visually-empty footprint tile (e.g. a
// windmill's blade tiles). Computed from sprite alpha here (offline) rather than
// at gameplay/load time: probing ~40 building textures at startup cost ~15-25ms,
// over budget. Writes Assets/Resources/buildingEdgeMasks.json; Db loads it.
//
// Re-run (Tools/Bake Building Edge Masks) after editing any building sprite —
// the masks are a baked artifact and won't auto-refresh on art changes.
public static class BuildingEdgeMaskBaker {
    const int   BodyPx       = 16;    // pixels per tile (PPU); a building sprite is nx*16 x ny*16
    const int   EdgeCols     = 2;     // outer columns sampled per side — leniency, the frame may sit 1px in
    const float EdgeCoverage = 0.5f;  // fraction of a tile's rows that must be opaque to count as a wall

    const string OutputPath = "Assets/Resources/buildingEdgeMasks.json";

    // Lightweight view of a buildingsDb entry — avoids deserializing into StructType,
    // whose [OnDeserialized] needs a loaded Db (jobByName etc.). Unknown JSON fields are ignored.
    class BldDef {
        public string name;
        public int nx;
        public int ny;
        public bool isTile;
        public bool isPlant;
        public object[] shapes;   // non-null/non-empty → variable-shape building (skip)
    }

    class EdgeMaskEntry {
        public string name;
        public int left;
        public int right;
    }

    [MenuItem("Tools/Bake Building Edge Masks", priority = 142)]
    public static void Bake() {
        TextAsset json = Resources.Load<TextAsset>("buildingsDb");
        if (json == null) { Debug.LogError("BuildingEdgeMaskBaker: buildingsDb not found in Resources"); return; }

        BldDef[] defs = JsonConvert.DeserializeObject<BldDef[]>(json.text);
        var entries = new List<EdgeMaskEntry>();
        int skipped = 0;

        foreach (BldDef d in defs) {
            if (d == null || string.IsNullOrEmpty(d.name)) continue;
            // Only plain buildings map cleanly to an nx*16 x ny*16 sprite.
            if (d.isTile || d.isPlant || (d.shapes != null && d.shapes.Length > 0)) continue;
            int nx = d.nx > 0 ? d.nx : 1;
            int ny = d.ny > 0 ? d.ny : 1;

            string clean = d.name.Replace(" ", "");
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/Sprites/Buildings/" + clean + ".png");
            if (tex == null || !tex.isReadable) { skipped++; continue; }
            if (tex.width != nx * BodyPx || tex.height != ny * BodyPx) { skipped++; continue; } // not footprint-aligned (animated sheet, custom PPU)

            Color32[] px = tex.GetPixels32();
            int left = 0, right = 0;
            for (int ty = 0; ty < ny; ty++)
                for (int tx = 0; tx < nx; tx++) {
                    int bit = ty * nx + tx;
                    if (EdgeOpaque(px, tex.width, tx, ty, fromLeft: true))  left  |= 1 << bit;
                    if (EdgeOpaque(px, tex.width, tx, ty, fromLeft: false)) right |= 1 << bit;
                }
            entries.Add(new EdgeMaskEntry { name = d.name, left = left, right = right });
        }

        File.WriteAllText(OutputPath, JsonConvert.SerializeObject(entries, Formatting.Indented));
        AssetDatabase.Refresh();
        Debug.Log($"BuildingEdgeMaskBaker: baked {entries.Count} building edge masks, skipped {skipped}. Wrote {OutputPath}");
    }

    // True if any of the outer EdgeCols columns of footprint tile (tx,ty) on one side is opaque
    // (alpha ≥ 128) over ≥ EdgeCoverage of the tile's BodyPx rows.
    static bool EdgeOpaque(Color32[] px, int texW, int tx, int ty, bool fromLeft) {
        int x0 = tx * BodyPx, y0 = ty * BodyPx;
        int need = Mathf.CeilToInt(BodyPx * EdgeCoverage);
        for (int c = 0; c < EdgeCols; c++) {
            int x = fromLeft ? (x0 + c) : (x0 + BodyPx - 1 - c);
            int opaque = 0;
            for (int r = 0; r < BodyPx; r++)
                if (px[(y0 + r) * texW + x].a >= 128) opaque++;
            if (opaque >= need) return true;
        }
        return false;
    }
}
