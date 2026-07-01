using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

// ── Building Edge Solidity Baker ───────────────────────────────────────
// Bakes, per building, three bitmasks of which footprint tiles present body on
// their LEFT / RIGHT / BOTTOM edge — read at runtime by StructType.EdgeSolid so a
// mount can't attach against a visually-empty footprint tile (e.g. a windmill's
// blade tiles). Left/Right gate side-mounts; Bottom gates ceiling-mounts (hanging
// lanterns). Computed from sprite alpha here (offline) rather than at gameplay/load
// time: probing ~40 building textures at startup cost ~15-25ms, over budget. Writes
// Assets/Resources/buildingEdgeMasks.json; Db loads it.
//
// Re-run (Tools/Bake Building Edge Solidity) after editing any building sprite —
// the masks are a baked artifact and won't auto-refresh on art changes.
public static class BuildingEdgeSolidityBaker {
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
        public int bottom;
    }

    [MenuItem("Tools/Bake Building Edge Solidity", priority = 142)]
    public static void Bake() {
        TextAsset json = Resources.Load<TextAsset>("buildingsDb");
        if (json == null) { Debug.LogError("BuildingEdgeSolidityBaker: buildingsDb not found in Resources"); return; }

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
            int left = 0, right = 0, bottom = 0;
            for (int ty = 0; ty < ny; ty++)
                for (int tx = 0; tx < nx; tx++) {
                    int bit = ty * nx + tx;
                    if (EdgeOpaque(px, tex.width, tx, ty, Edge.Left))   left   |= 1 << bit;
                    if (EdgeOpaque(px, tex.width, tx, ty, Edge.Right))  right  |= 1 << bit;
                    if (EdgeOpaque(px, tex.width, tx, ty, Edge.Bottom)) bottom |= 1 << bit;
                }
            entries.Add(new EdgeMaskEntry { name = d.name, left = left, right = right, bottom = bottom });
        }

        File.WriteAllText(OutputPath, JsonConvert.SerializeObject(entries, Formatting.Indented));
        AssetDatabase.Refresh();
        Debug.Log($"BuildingEdgeSolidityBaker: baked {entries.Count} building edge masks, skipped {skipped}. Wrote {OutputPath}");
    }

    enum Edge { Left, Right, Bottom }

    // True if any of the outer EdgeCols lines of footprint tile (tx,ty) on the given edge is opaque
    // (alpha ≥ 128) over ≥ EdgeCoverage of the perpendicular extent. GetPixels32 is bottom-up, so the
    // tile's BOTTOM edge is its lowest rows (r near 0) — matching the footprint convention where the
    // structure anchor (dy=0) is the bottom row.
    static bool EdgeOpaque(Color32[] px, int texW, int tx, int ty, Edge edge) {
        int x0 = tx * BodyPx, y0 = ty * BodyPx;
        int need = Mathf.CeilToInt(BodyPx * EdgeCoverage);
        for (int i = 0; i < EdgeCols; i++) {
            int opaque = 0;
            if (edge == Edge.Bottom) {
                int y = y0 + i;                                   // outer rows, from the bottom up
                for (int c = 0; c < BodyPx; c++)
                    if (px[y * texW + (x0 + c)].a >= 128) opaque++;
            } else {
                int x = edge == Edge.Left ? (x0 + i) : (x0 + BodyPx - 1 - i);
                for (int r = 0; r < BodyPx; r++)
                    if (px[(y0 + r) * texW + x].a >= 128) opaque++;
            }
            if (opaque >= need) return true;
        }
        return false;
    }
}
