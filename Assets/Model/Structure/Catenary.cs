using System.Collections.Generic;
using UnityEngine;

// Pure-math helper for the rope bridge's curve. The bridge sags between two
// endpoint posts at (xA, yA) and (xB, yB); sag amplitude is sagFraction * |dx|
// (horizontal delta only — euclidean span would let the curve dip below the
// lower endpoint for slanted bridges).
//
// Curve shape: y(t) = lerp(yLo, yHi, t) - sagFraction * dx * sin(π * t),
// where t = 0 at the leftmost endpoint and t = 1 at the rightmost. The sin-arch
// is a visually-faithful stand-in for the true cosh catenary at this scale —
// cheaper and pixel-identical in the play camera.
//
// Three responsibilities, all deterministic so placement validation, the
// bridge's tile claim, and teardown never diverge:
//  - YAt(...)              — y at a given x
//  - ClaimedTiles(...)     — integer tiles the curve passes through (inclusive
//                            of both endpoint tiles)
//  - WaypointPositions(...) — fractional (x,y) positions for interior nav
//                            waypoints. Endpoint posts are excluded — those
//                            are the post tile-nodes themselves.
public static class Catenary {

    // Horizontal delta between the two endpoint posts. A useful bridge needs
    // at least 2 — i.e. one mid-tile between the posts. Float-typed so the
    // same helper serves tile-coord (placement validation) and world-coord
    // (rope rendering) callers. Int callers auto-cast.
    public static float HorizontalDelta(float xA, float xB) {
        return Mathf.Abs(xB - xA);
    }

    // y-coordinate of the curve at horizontal position x. Behaviour outside
    // [min(xA,xB), max(xA,xB)] is undefined — callers bound-check first.
    //
    // Float-typed so callers can pass either tile coords (used by ClaimedTiles
    // when validating placement) or world coords (used by RopeBridge when
    // rendering the rope visual + placing nav waypoints at the post bases
    // instead of tile centres). Int → float is implicit at call sites.
    public static float YAt(float xA, float yA, float xB, float yB, float sagFraction, float x) {
        float lo = Mathf.Min(xA, xB);
        float hi = Mathf.Max(xA, xB);
        float dx = hi - lo;
        if (dx <= 0f) return (yA + yB) * 0.5f;
        float yLo = (xA <= xB) ? yA : yB;
        float yHi = (xA <= xB) ? yB : yA;
        float t = (x - lo) / dx;
        float baseY = Mathf.Lerp(yLo, yHi, t);
        float sag = sagFraction * dx;
        return baseY - sag * Mathf.Sin(Mathf.PI * t);
    }

    // Integer tiles the curve passes through, one per x-column from the left
    // post's x to the right post's x (inclusive on both ends). Each tile is
    // (x, floor(YAt(x))) — the tile the catenary point lies inside under the
    // convention that tile (tx, ty) covers world y ∈ [ty, ty+1). The two
    // endpoint posts are naturally included: at x=lo, YAt=yLo →
    // floor(yLo) = yLo; same at hi.
    //
    // Sampled at integer x only. Steep-slope curves (large |Δy|) may skip
    // tiles vertically between adjacent samples — placement constraints cap
    // |Δy| to keep this acceptable for v1.
    public static IEnumerable<(int x, int y)> ClaimedTiles(int xA, int yA, int xB, int yB, float sagFraction) {
        int lo = Mathf.Min(xA, xB);
        int hi = Mathf.Max(xA, xB);
        for (int x = lo; x <= hi; x++) {
            float y = YAt(xA, yA, xB, yB, sagFraction, x);
            yield return (x, Mathf.FloorToInt(y));
        }
    }

    // Interior waypoint positions for the nav chain, walking from the leftmost
    // post (lo) toward the rightmost (hi). Endpoints are NOT in the list — the
    // post tile-nodes serve as the chain's two end caps. Roughly two waypoints
    // per horizontal world-unit so the rendered curve reads smoothly and nav
    // cost approximates true path length.
    //
    // Order is monotonic in x (left→right). Returns an empty array when |dx| < 2.
    //
    // Float-typed — callers pass world coords when rendering the bridge so the
    // chain attaches to the post bases (xL + 0.5, yL - 0.5) rather than the
    // tile centre. The tile-coord overload is reachable via implicit int→float.
    public static Vector2[] WaypointPositions(float xA, float yA, float xB, float yB, float sagFraction) {
        float dx = HorizontalDelta(xA, xB);
        if (dx < 2f) return new Vector2[0];
        // ~2 waypoints per world unit. RoundToInt keeps integer-coord callers
        // producing the same density as before (5 → 9, 10 → 19); float callers
        // get proportional density without any special cases.
        int n = Mathf.Max(1, Mathf.RoundToInt(2f * dx) - 1);
        float lo = Mathf.Min(xA, xB);
        float yLo = (xA <= xB) ? yA : yB;
        float yHi = (xA <= xB) ? yB : yA;
        Vector2[] result = new Vector2[n];
        float sag = sagFraction * dx;
        for (int i = 0; i < n; i++) {
            float t = (i + 1f) / (n + 1f);
            float x = lo + t * dx;
            float y = Mathf.Lerp(yLo, yHi, t) - sag * Mathf.Sin(Mathf.PI * t);
            result[i] = new Vector2(x, y);
        }
        return result;
    }
}
