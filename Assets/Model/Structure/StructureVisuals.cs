using System.Collections.Generic;
using UnityEngine;

// Shared placement-visual maths for structures, blueprints, and the build-mode preview ghost.
// Centralises three concerns that were copied across Structure.cs, Blueprint.cs, and
// MouseController.cs:
//   1. Multi-tile sprites use centre pivots, so the GameObject transform must sit at the
//      visual centre of the (nx × ny) footprint anchored at (x, y) — otherwise tall
//      buildings render half a tile below their anchor row.
//   2. Roads (depth 3) sit slightly below the tile centre so they read as floor-level paint.
//   3. Rotation in 90° clockwise steps applies as a Z-axis quaternion. Rotation is only
//      meaningful for 1×1 buildings in v1 (multi-tile rotation would visually overflow
//      the footprint without remapping nx/ny).
//
// Also: shape-aware sprite loading for variable-shape buildings — see LoadShapeSprite.
public static class StructureVisuals {
    // Visual centre of the footprint, with the depth-3 floor offset baked in.
    // z defaults to 0; the build preview overrides to -1 to render above placed structures.
    public static Vector3 PositionFor(StructType st, int x, int y, float z = 0f) {
        float vx = st.nx > 1 ? x + (st.nx - 1) / 2.0f : x;
        float vy = st.ny > 1 ? y + (st.ny - 1) / 2.0f : y;
        float yOffset = st.depth == 3 ? -1f / 8f : 0f;
        return new Vector3(vx, vy + yOffset, z);
    }

    // Quaternion for a 0..3 rotation step (90° clockwise per step). Returns identity at 0
    // so callers can skip the assignment in the common case if they want to.
    public static Quaternion RotationFor(int rotation) {
        return rotation != 0 ? Quaternion.Euler(0, 0, -90f * rotation) : Quaternion.identity;
    }

    // Resolves the anchor sprite for a structure or blueprint, with the shape-aware vs.
    // legacy lookup baked in. Returns the loaded sprite or a default-fallback sprite;
    // `wasFallback` lets the caller apply sliced-mode sizing for the missing-sprite case
    // without re-checking nullness. Centralises the fallback path so Blueprint and Structure
    // can't drift on the missing-sprite handling.
    public static Sprite ResolveAnchorSprite(StructType st, Shape shape, out bool wasFallback) {
        Sprite s = st.HasShapes ? LoadShapeSprite(st, shape, 0) : st.LoadSprite();
        wasFallback = s == null;
        return s ?? Resources.Load<Sprite>("Sprites/Buildings/default");
    }

    // Resolves the per-tile sprite for a shape-aware structure at a given dy offset.
    // Conventions:
    //   - shape.ny == 1: returns the base `{name}` sprite (1-tall shapes look like the
    //     existing single-tile structure — no _b/_t suffix needed).
    //   - shape.ny  > 1: dy=0 → `_b` (anchor), dy=ny-1 → `_t` (top), else `_m` (middle).
    //   - Falls back to the base `{name}` sprite if a variant PNG is missing, with a
    //     one-time error log so the missing asset is loud during development.
    // (Horizontal-only and grid shapes — nx>1 — are not handled in v1; we log an error
    // if such a shape is requested.)
    public static Sprite LoadShapeSprite(StructType st, Shape shape, int dy) {
        string baseName = "Sprites/Buildings/" + st.name.Replace(" ", "");
        if (shape.nx > 1 && shape.ny > 1) {
            LogShapeMissOnce(st.name + ":grid", $"Shape sprite for grid (nx>1, ny>1) not supported in v1 — using base sprite for {st.name}");
            return Resources.Load<Sprite>(baseName);
        }
        if (shape.ny == 1) return Resources.Load<Sprite>(baseName);

        string suffix = dy == 0 ? "_b"
                      : dy == shape.ny - 1 ? "_t"
                      : "_m";
        Sprite s = Resources.Load<Sprite>(baseName + suffix);
        if (s != null && s.texture != null) return s;
        LogShapeMissOnce(st.name + suffix, $"Shape sprite missing: {baseName}{suffix} — falling back to {st.name}");
        return Resources.Load<Sprite>(baseName);
    }

    private static readonly HashSet<string> _shapeSpriteMissLog = new HashSet<string>();
    private static void LogShapeMissOnce(string key, string msg) {
        if (_shapeSpriteMissLog.Add(key)) Debug.LogError(msg);
    }
}
