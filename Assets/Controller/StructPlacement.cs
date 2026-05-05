using UnityEngine;

// Static helper that decides whether a StructType can be placed on a given tile.
// Extracted from BuildPanel so that the rule is testable and reusable outside the UI.
// All placement logic should live here; BuildPanel.CanPlaceHere delegates to this.
public static class StructPlacement {
    public static bool CanPlaceHere(StructType st, Tile tile, bool mirrored = false, int shapeIndex = 0) {
        World world = World.instance;

        if (tile.GetBlueprintAt(st.depth) != null) return false;

        // A mustBeSolidTile requirement on the placement tile itself (cached as
        // `requiresSolidTilePlacement` in OnDeserialized) is the author's explicit signal that this
        // StructType is meant to occupy a solid tile (e.g. mineshaft → mines stone and leaves a ladder).
        // Skips the default "non-empty tile" and standability rejections below — the requirement loop
        // further down still enforces the solid-tile check itself.
        bool placementTileMustBeSolid = st.requiresSolidTilePlacement;

        if (tile.type.id != 0 && st.name != "empty" && st.requiredTileName == null && !placementTileMustBeSolid) return false;
        // requiredTileName matches either a specific tile name or the tile's group
        // (e.g. quarry's "stone" requirement accepts limestone/granite/slate).
        if (st.requiredTileName != null
            && tile.type.name  != st.requiredTileName
            && tile.type.group != st.requiredTileName) return false;

        // Footprint dimensions for collision: every tile in the visual footprint must be
        // empty, so a 2×4 windmill's collision check covers all 8 tiles. Matches the claim
        // footprint that Structure / Blueprint write into tile.structs[depth].
        Shape shape = st.GetShape(shapeIndex);
        bool shapeAware = st.HasShapes;
        int fnx = shapeAware ? shape.nx : st.nx;
        int fny = shapeAware ? shape.ny : Mathf.Max(1, st.ny);

        if (!st.isTile) {
            if (st.isPlant && tile.structs[0] != null) return false;
            if (!st.isPlant) {
                for (int dy = 0; dy < fny; dy++) {
                    for (int dx = 0; dx < fnx; dx++) {
                        Tile t = world.GetTileAt(tile.x + dx, tile.y + dy);
                        if (t == null) return false;
                        // Per-depth collision only. Plants occupy slot 0 across their full vertical
                        // footprint, so plants still block other depth-0 placements via this same
                        // check. Other depths (shafts behind a bamboo, road under a sapling, etc.)
                        // are allowed to coexist — visual clipping inside tall plants is accepted.
                        if (t.structs[st.depth] != null || t.GetBlueprintAt(st.depth) != null) return false;
                    }
                }
            }
        }

        // Standability check on the bottom row.
        // Default: one "body" tile must be supported (left end if !mirrored, right end if mirrored).
        // edgeSupported: BOTH ends of the footprint must be supported, with the middle free to
        // hang in mid-air (e.g. tarps strung between two posts).
        if (st.name != "empty" && st.requiredTileName == null && !placementTileMustBeSolid) {
            if (st.edgeSupported) {
                int leftX  = tile.x;
                int rightX = tile.x + fnx - 1;
                bool leftOk  = world.graph.nodes[leftX,  tile.y].standable
                            || SupportedByBlueprintBelow(leftX,  tile.y);
                bool rightOk = world.graph.nodes[rightX, tile.y].standable
                            || SupportedByBlueprintBelow(rightX, tile.y);
                if (!leftOk || !rightOk) return false;
            } else {
                int bodyDx = mirrored ? fnx - 1 : 0;
                if (!world.graph.nodes[tile.x + bodyDx, tile.y].standable
                    && !SupportedByBlueprintBelow(tile.x + bodyDx, tile.y)) return false;
            }
        }

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
                if (req.mustBeSolidTile && !t.type.solid) return false;
                if (req.mustBeOpenSkyAbove && !world.IsExposedAbove(t.x, t.y)) return false;
                if (req.requiredTileName != null
                    && t.type.name  != req.requiredTileName
                    && t.type.group != req.requiredTileName) return false;
            }
        }

        return true;
    }

    // Returns true if any blueprint on the tile below (x, y-1) would provide solid-top support once built.
    // Used to allow stacking blueprints before their support is constructed.
    private static bool SupportedByBlueprintBelow(int x, int y) {
        Tile below = World.instance.GetTileAt(x, y - 1);
        if (below == null) return false;
        for (int d = 0; d < Tile.NumDepths; d++) {
            Blueprint bp = below.GetBlueprintAt(d);
            if (bp != null && !bp.cancelled && bp.structType.solidTop) return true;
        }
        return false;
    }
}
