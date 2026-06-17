using UnityEngine;

// Static helper that decides whether a StructType can be placed on a given tile.
// Extracted from BuildPanel so that the rule is testable and reusable outside the UI.
//
// Two parallel entry points:
//   - CanPlaceHere / CanPlaceTwoPoint        → bool, for the hot path (ghost preview).
//   - GetPlacementFailReason / GetTwoPointFailReason → string-or-null, for failed
//     click attempts where we want to show the player WHY it failed.
//
// The bool methods are thin wrappers around the reason methods; reason strings are
// only allocated when placement fails, so success paths (every-frame ghost preview)
// stay allocation-free.
public static class StructPlacement {
    public static bool CanPlaceHere(StructType st, Tile tile, bool mirrored = false, int shapeIndex = 0, int rotation = 0) {
        return GetPlacementFailReason(st, tile, mirrored, shapeIndex, rotation) == null;
    }

    public static bool CanPlaceTwoPoint(StructType st, Tile a, Tile b) {
        return GetTwoPointFailReason(st, a, b) == null;
    }

    // Returns null when placement is OK; otherwise a short, lowercase, player-facing
    // reason string. Only allocates on rejection.
    public static string GetPlacementFailReason(StructType st, Tile tile, bool mirrored = false, int shapeIndex = 0, int rotation = 0) {
        World world = World.instance;

        if (tile.GetBlueprintAt(st.depth) != null) return "already a blueprint here";

        // Side-mounted structures (ladder_side, bracket): hang on a wall, no floor needed.
        // Skip standability and the rest of the generic rule set — they don't use
        // requiredTileName, doors, tileRequirements, or shapes. Only require (a) the target
        // tile is air-and-empty at its depth and (b) the wall tile on the mounted side is
        // solid (terrain or a building face). Because the wall check only accepts terrain or
        // a depth-0 building, a bracket can't mount on another bracket (depth 1) — cantilevers
        // extend exactly one tile per level, no chaining.
        // dir convention: mirrored=true → wall on right (sprite flipped); mirrored=false → wall on left.
        if (st.sideMounted) {
            if (tile.type.id != 0) return "tile is not empty";
            if (tile.structs[st.depth] != null) return "footprint is blocked";
            if (!SideMountWallPresent(tile, mirrored)) return "needs a wall on this side";
            return null;
        }

        // A mustBeSolidTile requirement on the placement tile itself (cached as
        // `requiresSolidTilePlacement` in OnDeserialized) is the author's explicit signal that this
        // StructType is meant to occupy a solid tile (e.g. mineshaft → mines stone and leaves a ladder).
        // Skips the default "non-empty tile" and standability rejections below — the requirement loop
        // further down still enforces the solid-tile check itself.
        bool placementTileMustBeSolid = st.requiresSolidTilePlacement;

        if (tile.type.id != 0 && st.name != "empty" && st.requiredTileName == null && !placementTileMustBeSolid) return "tile is not empty";
        // Block mining tiles that sit under a `preservesTile` structure (burrow). The host
        // structure relies on the tile staying its authored type for grass / support / snow;
        // mining it out would silently break those invariants and leave a broken burrow.
        if (st.isTile && st.name == "empty") {
            for (int d = 0; d < Tile.NumDepths; d++) {
                Structure s = tile.structs[d];
                if (s != null && s.structType.preservesTile) return $"would disturb {s.structType.name}";
            }
        }
        // Stone extraction is gated behind the Mining technology. Mining out a stone-group
        // tile (limestone/granite/slate) — whether via the "mine tile" action (`empty`) or a
        // tile-occupying structure like the mineshaft (`requiresSolidTilePlacement`) — requires
        // Mining to be unlocked. Earth tiles (dirt/sand/clay) stay free so burrows/digging pits
        // need no tech. The quarry is gated separately via `defaultLocked`, so it never reaches
        // this path. `minesTile` mirrors the same predicate in Blueprint.Complete. Permissive when
        // ResearchSystem is absent (e.g. unit-test contexts) — don't block what can't be checked.
        bool minesTile = (st.isTile && st.name == "empty") || placementTileMustBeSolid;
        if (minesTile && tile.type.group == "stone"
            && ResearchSystem.instance != null && !ResearchSystem.instance.IsUnlockedByName("Mining"))
            return "needs Mining technology";

        // requiredTileName matches either a specific tile name or the tile's group
        // (e.g. quarry's "stone" requirement accepts limestone/granite/slate).
        if (st.requiredTileName != null
            && tile.type.name  != st.requiredTileName
            && tile.type.group != st.requiredTileName) return $"needs {st.requiredTileName} tile";

        // Footprint dimensions for collision: every tile in the visual footprint must be
        // empty, so a 2×4 windmill's collision check covers all 8 tiles. Matches the claim
        // footprint that Structure / Blueprint write into tile.structs[depth].
        Shape shape = st.GetShape(shapeIndex);
        bool shapeAware = st.HasShapes;
        int fnx = shapeAware ? shape.nx : st.nx;
        int fny = shapeAware ? shape.ny : Mathf.Max(1, st.ny);

        if (!st.isTile) {
            if (st.isPlant && tile.structs[0] != null) return "tile is occupied";
            if (!st.isPlant) {
                for (int dy = 0; dy < fny; dy++) {
                    for (int dx = 0; dx < fnx; dx++) {
                        Tile t = world.GetTileAt(tile.x + dx, tile.y + dy);
                        if (t == null) return "footprint extends off the map";
                        // Per-depth collision only. Plants occupy slot 0 across their full vertical
                        // footprint, so plants still block other depth-0 placements via this same
                        // check. Other depths (shafts behind a bamboo, road under a sapling, etc.)
                        // are allowed to coexist — visual clipping inside tall plants is accepted.
                        if (t.structs[st.depth] != null || t.GetBlueprintAt(st.depth) != null) return "footprint is blocked";
                    }
                }
            }
        }

        // Standability check on the bottom row.
        //  - Default: EVERY column of the footprint's bottom row must be supported (solid ground
        //    or a built/queued solidTop structure beneath). Matches Blueprint.IsSuspended.
        //  - edgeSupported: only the two end columns must be supported, the middle may hang in
        //    mid-air (tarp). Rope-bridge posts are 1×1 twoClick placements validated per-post,
        //    so "both ends supported" already holds for them via this same all-columns rule.
        //  - hasStandableRequirement: the type names exactly which tiles need support via
        //    mustBeStandable tileRequirements (pump → just the building tile, spout overhangs
        //    water), so the generic check is skipped and those reqs (below) decide it.
        // Power shafts get an extra support path: a shaft that hooks onto an existing shaft
        // (axis-compatible neighbour) is self-bearing, like a rigid axle cantilevering off the
        // run. So a vertical shaft stacks on the vertical / turn / 4-way shaft below it, etc.
        // This only relaxes the shaft's OWN support requirement — shafts aren't solidTop, so
        // they still don't bear other structures above. Falls through to the solid-ground /
        // solid-top rules below when it doesn't connect. Counts queued shaft blueprints so a
        // whole run can be planned in one pass (Blueprint.IsSuspended keeps it suspended until a
        // real shaft anchors it).
        bool shaftConnected = PowerShaft.IsShaft(st)
                && PowerShaft.ConnectsToShaft(st, tile, rotation, mirrored, includeBlueprints: true);

        if (st.name != "empty" && st.requiredTileName == null && !placementTileMustBeSolid
                && !st.hasStandableRequirement && !shaftConnected) {
            if (st.edgeSupported) {
                int leftX  = tile.x;
                int rightX = tile.x + fnx - 1;
                bool leftOk  = world.graph.nodes[leftX,  tile.y].standable
                            || SupportedByBlueprintBelow(leftX,  tile.y);
                bool rightOk = world.graph.nodes[rightX, tile.y].standable
                            || SupportedByBlueprintBelow(rightX, tile.y);
                if (!leftOk || !rightOk) return "both ends need support";
            } else {
                for (int dx = 0; dx < fnx; dx++) {
                    if (!world.graph.nodes[tile.x + dx, tile.y].standable
                        && !SupportedByBlueprintBelow(tile.x + dx, tile.y)) return "needs solid ground below";
                }
            }
        }

        // Door-approach validation: at least one declared door must open onto a standable
        // tile so mice can actually reach the building. Buildings with multiple doors
        // (e.g. digging pit with top/left/right) are placeable as long as ONE side is
        // reachable — useful when the surrounding world only has one open side. Without
        // this, doored buildings (burrow, shack) can be placed facing solid dirt or
        // empty air and become unenterable post-build. Mirroring applies the same flip
        // Structure.cs uses when wiring the door at construction time.
        if (st.doors != null && st.doors.Length > 0) {
            bool anyDoorOpen = false;
            for (int i = 0; i < st.doors.Length; i++) {
                Door door = st.doors[i];
                int doorDx = mirrored ? (st.nx - 1 - door.dx) : door.dx;
                string side = door.side;
                if (mirrored) {
                    if (side == "left") side = "right";
                    else if (side == "right") side = "left";
                }
                int aX = tile.x + doorDx, aY = tile.y + door.dy;
                switch (side) {
                    case "left":   aX -= 1; break;
                    case "right":  aX += 1; break;
                    case "top":    aY += 1; break;
                    case "bottom": aY -= 1; break;
                }
                if (aX < 0 || aX >= world.nx || aY < 0 || aY >= world.ny) continue;
                if (!world.graph.nodes[aX, aY].standable) continue;
                anyDoorOpen = true;
                break;
            }
            if (!anyDoorOpen) return "no door has open approach";
        }

        // Data-driven per-tile constraints from JSON.
        // When mirrored, X offsets are flipped: effectiveDx = (nx - 1 - dx).
        if (st.tileRequirements != null) {
            foreach (TileRequirement req in st.tileRequirements) {
                int effectiveDx = mirrored ? (st.nx - 1 - req.dx) : req.dx;
                Tile t = world.GetTileAt(tile.x + effectiveDx, tile.y + req.dy);
                if (t == null) return "footprint extends off the map";
                if (req.mustBeStandable && !world.graph.nodes[t.x, t.y].standable) return "needs standable tile";
                if (req.mustHaveWater && t.water == 0) return "needs water";
                // Plant check runs before the generic mustBeEmpty so a plant gets the
                // more specific message even when both flags are set on the same req.
                if (req.mustNotBePlant && t.structs[0] is Plant) return "plant resting on this tile";
                if (req.mustBeEmpty && t.structs[0] != null) return "something resting on this tile";
                // A requirement on a tile below the origin (dy < 0, e.g. fireplace) reads as
                // "below"; one on the placement tile itself (dy == 0, e.g. mineshaft sunk into
                // the tile) keeps the bare phrasing.
                if (req.mustBeSolidTile && !t.type.solid) return req.dy < 0 ? "needs solid tiles below" : "needs solid tile";
                if (req.mustBeOpenSkyAbove && !world.IsExposedAbove(t.x, t.y)) return "needs open sky above";
                if (req.requiredTileName != null
                    && t.type.name  != req.requiredTileName
                    && t.type.group != req.requiredTileName) return $"needs {req.requiredTileName} tile";
            }
        }

        return null;
    }

    // Validation for two-click placements (rope bridge). Both posts must be standable
    // 1×1 spots, and every integer tile the catenary passes through (between the two
    // posts, exclusive) must be empty at depth 2 — no structure, no blueprint, no
    // solid tile blocking the rope.
    //
    // The "catenary sample at each x-column" claim function (Catenary.ClaimedTiles)
    // is the SAME function the bridge will use at construction time and at teardown,
    // so validation never disagrees with what gets occupied.
    public static string GetTwoPointFailReason(StructType st, Tile a, Tile b) {
        if (st == null || a == null || b == null) return "invalid placement";
        World world = World.instance;
        int dx = (int)Catenary.HorizontalDelta(a.x, b.x);
        int dy = Mathf.Abs(b.y - a.y);
        int minDx = st.minDx > 0 ? st.minDx : 3;
        int maxDx = st.maxDx > 0 ? st.maxDx : 20;
        int maxDy = st.maxDy > 0 ? st.maxDy : 5;
        if (dx < minDx) return "too close";
        if (dx > maxDx) return "too far";
        if (dy > maxDy) return "too steep";
        if (a == b)     return "endpoints overlap";

        // Both posts: standard single-tile placement check. GetPlacementFailReason validates
        // empty-at-depth, standability, and no-blueprint-collision in one pass; prefix the
        // returned reason so the player knows which post failed.
        string aReason = GetPlacementFailReason(st, a);
        if (aReason != null) return $"first post: {aReason}";
        string bReason = GetPlacementFailReason(st, b);
        if (bReason != null) return $"second post: {bReason}";

        // Catenary tile claim must be clear. The two post tiles are themselves in
        // the claim — GetPlacementFailReason already cleared them, so skipping the endpoints
        // avoids redundant work.
        float sagFraction = st.sagFraction > 0f ? st.sagFraction : 0.15f;
        foreach ((int tx, int ty) in Catenary.ClaimedTiles(a.x, a.y, b.x, b.y, sagFraction)) {
            if ((tx == a.x && ty == a.y) || (tx == b.x && ty == b.y)) continue;
            Tile t = world.GetTileAt(tx, ty);
            if (t == null)                          return "rope path goes off the map";
            if (t.type.solid)                       return "rope path blocked by solid tile";
            if (t.structs[st.depth] != null)        return "rope path blocked";
            if (t.GetBlueprintAt(st.depth) != null) return "rope path blocked by blueprint";
        }
        return null;
    }

    // True if a side-mounted structure (ladder_side, bracket) has a solid wall to lean on
    // its mounted side — a natural/built solid tile, or a building whose sprite has body on
    // the facing edge (never a plant, never a visually-empty footprint tile). This is the
    // SINGLE source of truth for side-mount support: both placement (above) and
    // Blueprint.IsSuspended call it, so a side-mounted blueprint is always completeable under
    // the same rule it was placed with. dir: mirrored=true → wall on right; mirrored=false → wall on left.
    public static bool SideMountWallPresent(Tile tile, bool mirrored) {
        World world = World.instance;
        int dir = mirrored ? +1 : -1;
        Tile wall = world.GetTileAt(tile.x + dir, tile.y);
        if (wall == null) return false;
        if (wall.type.solid) return true;
        Structure s = wall.structs[0];
        if (s == null || s is Plant) return false;
        return s.structType.SideEdgeSolid(wall.x - s.x, wall.y - s.y, !mirrored, s.mirrored);
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
