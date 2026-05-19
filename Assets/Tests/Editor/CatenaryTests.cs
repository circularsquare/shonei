using NUnit.Framework;
using UnityEngine;
using System.Linq;
using System.Collections.Generic;

// EditMode tests for Catenary — the pure-math helper that drives rope bridge
// placement, tile claiming, and waypoint chain construction. Covers the
// invariants that the rest of the bridge code depends on staying true:
//  - endpoints land on their own tiles in the claim
//  - sag dips below the chord at midspan and never below the LOWER endpoint
//  - claim/waypoint ordering is monotonic in x regardless of which endpoint
//    was clicked first
//  - left-vs-right click order doesn't change the claimed-tile set
[TestFixture]
public class CatenaryTests {

    const float Sag = 0.15f;
    const float Tol = 1e-4f;

    // ── YAt: endpoint anchoring ────────────────────────────────────────
    [Test]
    public void YAt_AtLeftPost_EqualsLeftPostY(){
        Assert.That(Catenary.YAt(2, 10, 8, 12, Sag, 2), Is.EqualTo(10f).Within(Tol));
    }

    [Test]
    public void YAt_AtRightPost_EqualsRightPostY(){
        Assert.That(Catenary.YAt(2, 10, 8, 12, Sag, 8), Is.EqualTo(12f).Within(Tol));
    }

    // The catenary should sag BELOW the straight-line midpoint.
    [Test]
    public void YAt_AtMidspan_DipsBelowChord(){
        float mid = Catenary.YAt(0, 10, 10, 10, Sag, 5);
        Assert.That(mid, Is.LessThan(10f));
        // sag = 0.15 * 10 = 1.5; sin(π/2) = 1 → y = 10 - 1.5 = 8.5
        Assert.That(mid, Is.EqualTo(8.5f).Within(Tol));
    }

    // ── Sag from horizontal delta only (reviewer's catch) ──────────────
    // For a steep bridge (small Δx, larger |Δy|), sag must come from |Δx|
    // alone, otherwise the curve dips below the lower endpoint — looks like
    // the rope went through the floor.
    [Test]
    public void YAt_NeverDipsBelowLowerEndpoint_ForSteepBridge(){
        // posts at (0,10) and (3,5) — Δx=3, Δy=5. If sag were drawn from
        // euclidean length (~5.83) it'd be ~0.87 here, dipping to ~6.6 at
        // midspan — well below the chord but FINE as long as sag uses |Δx|=3
        // (sag=0.45), giving midspan y ≈ 7.5 - 0.45 = 7.05, still above 5.
        int n = 30;
        for (int i = 0; i <= n; i++) {
            float x = 0 + (3f / n) * i;
            float y = Catenary.YAt(0, 10, 3, 5, Sag, x);
            Assert.That(y, Is.GreaterThanOrEqualTo(5f - Tol),
                $"y={y} at x={x} dipped below lower post y=5");
        }
    }

    // ── ClaimedTiles ───────────────────────────────────────────────────
    [Test]
    public void ClaimedTiles_IncludesBothEndpointTiles(){
        var tiles = Catenary.ClaimedTiles(2, 10, 8, 12, Sag).ToList();
        Assert.That(tiles, Does.Contain((2, 10)));
        Assert.That(tiles, Does.Contain((8, 12)));
    }

    [Test]
    public void ClaimedTiles_OneTilePerXColumn(){
        var tiles = Catenary.ClaimedTiles(2, 10, 8, 12, Sag).ToList();
        // x ranges 2..8 inclusive → 7 columns → 7 tiles
        Assert.That(tiles.Count, Is.EqualTo(7));
        var xs = tiles.Select(t => t.x).Distinct().ToList();
        Assert.That(xs.Count, Is.EqualTo(7));
    }

    // Click order shouldn't affect the geometric footprint — the bridge from
    // A to B is the same set of tiles as the bridge from B to A.
    [Test]
    public void ClaimedTiles_IsSymmetricInClickOrder(){
        var ab = Catenary.ClaimedTiles(2, 10, 8, 12, Sag).OrderBy(t => t.x).ToList();
        var ba = Catenary.ClaimedTiles(8, 12, 2, 10, Sag).OrderBy(t => t.x).ToList();
        CollectionAssert.AreEqual(ab, ba);
    }

    // ── WaypointPositions ──────────────────────────────────────────────
    [Test]
    public void WaypointPositions_AreInteriorOnly(){
        // For posts at x=2 and x=8 (Δx=6), no waypoint should land at x≤2 or x≥8.
        var wps = Catenary.WaypointPositions(2, 10, 8, 12, Sag);
        foreach (Vector2 wp in wps) {
            Assert.That(wp.x, Is.GreaterThan(2f).And.LessThan(8f),
                $"waypoint at {wp} is not strictly interior");
        }
    }

    [Test]
    public void WaypointPositions_AreMonotonicInX(){
        // Walking direction should be left→right regardless of click order.
        var wpsAB = Catenary.WaypointPositions(2, 10, 8, 12, Sag);
        for (int i = 1; i < wpsAB.Length; i++)
            Assert.That(wpsAB[i].x, Is.GreaterThan(wpsAB[i - 1].x));

        var wpsBA = Catenary.WaypointPositions(8, 12, 2, 10, Sag);
        for (int i = 1; i < wpsBA.Length; i++)
            Assert.That(wpsBA[i].x, Is.GreaterThan(wpsBA[i - 1].x));
    }

    [Test]
    public void WaypointPositions_Density_IsTwoMinusOnePerDelta(){
        // Documented contract: n = 2 * |Δx| - 1 interior waypoints.
        Assert.That(Catenary.WaypointPositions(0, 0, 5, 0, Sag).Length, Is.EqualTo(9));
        Assert.That(Catenary.WaypointPositions(0, 0, 10, 0, Sag).Length, Is.EqualTo(19));
    }

    [Test]
    public void WaypointPositions_EmptyForTooShortBridge(){
        // Δx < 2 → no useful interior — function returns empty.
        Assert.That(Catenary.WaypointPositions(5, 10, 5, 10, Sag).Length, Is.EqualTo(0));
        Assert.That(Catenary.WaypointPositions(5, 10, 6, 10, Sag).Length, Is.EqualTo(0));
    }

    // ── HorizontalDelta ────────────────────────────────────────────────
    [TestCase(2, 8, 6)]
    [TestCase(8, 2, 6)]
    [TestCase(5, 5, 0)]
    public void HorizontalDelta_IsAbsValue(int xA, int xB, int expected){
        Assert.That(Catenary.HorizontalDelta(xA, xB), Is.EqualTo(expected));
    }
}
