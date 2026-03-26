/// <summary>
/// Static helper that decides whether a StructType can be placed on a given tile.
/// Extracted from BuildPanel so that the rule is testable and reusable outside the UI.
/// All placement logic should live here; BuildPanel.CanPlaceHere delegates to this.
/// </summary>
public static class StructPlacement {
    public static bool CanPlaceHere(StructType st, Tile tile, bool mirrored = false) {
        World world = World.instance;

        if (tile.GetBlueprintAt(st.depth) != null) return false;

        if (tile.type.id != 0 && st.name != "empty" && st.requiredTileName == null) return false;
        if (st.requiredTileName != null && tile.type.name != st.requiredTileName) return false;

        if (!st.isTile) {
            if (st.isPlant && tile.structs[0] != null) return false;
            if (!st.isPlant) {
                for (int i = 0; i < st.nx; i++) {
                    Tile t = world.GetTileAt(tile.x + i, tile.y);
                    if (t == null) return false;
                    if (t.structs[st.depth] != null || t.GetBlueprintAt(st.depth) != null) return false;
                }
            }
        }

        // When mirrored, the "body" side of the building is at (nx-1) rather than 0, so
        // standability must be checked there instead of the anchor.
        int bodyDx = mirrored ? st.nx - 1 : 0;
        if (st.name != "empty" && st.requiredTileName == null
            && !world.graph.nodes[tile.x + bodyDx, tile.y].standable
            && !SupportedByBlueprintBelow(tile.x + bodyDx, tile.y)) return false;

        // Data-driven per-tile constraints from JSON.
        // When mirrored, X offsets are flipped: effectiveDx = (nx - 1 - dx).
        if (st.tileRequirements != null) {
            foreach (TileRequirement req in st.tileRequirements) {
                int effectiveDx = mirrored ? (st.nx - 1 - req.dx) : req.dx;
                Tile t = world.GetTileAt(tile.x + effectiveDx, tile.y + req.dy);
                if (t == null) return false;
                if (req.mustBeStandable && !world.graph.nodes[t.x, t.y].standable) return false;
                if (req.mustHaveWater && t.water == 0) return false;
                if (req.mustBeEmpty && t.structs[0] != null) return false;
                if (req.requiredTileName != null && t.type.name != req.requiredTileName) return false;
            }
        }

        return true;
    }

    // Returns true if any blueprint on the tile below (x, y-1) would provide solid-top support once built.
    // Used to allow stacking blueprints before their support is constructed.
    private static bool SupportedByBlueprintBelow(int x, int y) {
        Tile below = World.instance.GetTileAt(x, y - 1);
        if (below == null) return false;
        for (int d = 0; d < 4; d++) {
            Blueprint bp = below.GetBlueprintAt(d);
            if (bp != null && !bp.cancelled && bp.structType.solidTop) return true;
        }
        return false;
    }
}
