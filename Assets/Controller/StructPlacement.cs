/// <summary>
/// Static helper that decides whether a StructType can be placed on a given tile.
/// Extracted from BuildPanel so that the rule is testable and reusable outside the UI.
/// All placement logic should live here; BuildPanel.CanPlaceHere delegates to this.
/// </summary>
public static class StructPlacement {
    public static bool CanPlaceHere(StructType st, Tile tile) {
        World world = World.instance;

        if (tile.GetBlueprintAt(st.depth) != null) return false;

        if (tile.type.id != 0 && st.name != "empty" && st.requiredTileName == null) return false;
        if (st.requiredTileName != null && tile.type.name != st.requiredTileName) return false;

        if (!st.isTile) {
            if (st.isPlant && tile.building != null) return false;
            if (!st.isPlant) {
                for (int i = 0; i < st.nx; i++) {
                    Tile t = world.GetTileAt(tile.x + i, tile.y);
                    if (t == null) return false;
                    if (t.structs[st.depth] != null || t.GetBlueprintAt(st.depth) != null) return false;
                }
            }
        }

        if (st.name != "empty" && st.requiredTileName == null && !world.graph.nodes[tile.x, tile.y].standable) return false;

        // Data-driven per-tile constraints from JSON
        if (st.tileRequirements != null) {
            foreach (TileRequirement req in st.tileRequirements) {
                Tile t = world.GetTileAt(tile.x + req.dx, tile.y + req.dy);
                if (t == null) return false;
                if (req.mustBeStandable && !world.graph.nodes[t.x, t.y].standable) return false;
                if (req.mustHaveWater && t.water == 0) return false;
                if (req.mustBeEmpty && t.structs[0] != null) return false;
                if (req.requiredTileName != null && t.type.name != req.requiredTileName) return false;
            }
        }

        return true;
    }
}
