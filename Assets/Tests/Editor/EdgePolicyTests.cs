using NUnit.Framework;
using UnityEngine;

// EditMode tests for EdgePolicy and its constant-cost subclasses (LadderPolicy,
// CliffPolicy, StairPolicy, WaypointApproachPolicy). Covers the singleton wiring,
// GetEdgeInfo cost/length values for each constant policy, the Euclidean computation
// in WaypointApproachPolicy, and the default virtual flags (PreventFall, SuspendsLerp)
// inherited from the base class.
//
// ── Deferred to integration tests ────────────────────────────────────
// ElevatorEdgePolicy lives in Structure/Elevator.cs and carries per-instance state
// tied to a live Elevator; testing it requires a Structure fixture. OnApproach /
// OnPathCommit / OnPathRelease are no-ops on the constant policies (base virtual
// returns/does nothing), so there's nothing to assert beyond "doesn't throw" — kept
// out to avoid clutter.
[TestFixture]
public class EdgePolicyTests {

    // ── Singleton instances ────────────────────────────────────────────
    [Test]
    public void LadderPolicy_Instance_IsNonNull(){
        Assert.That(LadderPolicy.Instance, Is.Not.Null);
    }

    [Test]
    public void CliffPolicy_Instance_IsNonNull(){
        Assert.That(CliffPolicy.Instance, Is.Not.Null);
    }

    [Test]
    public void StairPolicy_Instance_IsNonNull(){
        Assert.That(StairPolicy.Instance, Is.Not.Null);
    }

    [Test]
    public void WaypointApproachPolicy_Instance_IsNonNull(){
        Assert.That(WaypointApproachPolicy.Instance, Is.Not.Null);
    }

    [Test]
    public void Singletons_AreUniquePerType(){
        // Sanity: no shared reference across the four policy types.
        EdgePolicy[] all = { LadderPolicy.Instance, CliffPolicy.Instance,
                             StairPolicy.Instance, WaypointApproachPolicy.Instance };
        for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
                Assert.That(all[i], Is.Not.SameAs(all[j]),
                    $"policy[{i}] and policy[{j}] share the same instance");
    }

    // ── LadderPolicy.GetEdgeInfo ───────────────────────────────────────
    [Test]
    public void LadderPolicy_GetEdgeInfo_ReturnsConstantCostAndLength(){
        Node a = new Node(3f, 5f);
        Node b = new Node(3f, 6f);
        var (cost, length) = LadderPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(2.0f));
        Assert.That(length, Is.EqualTo(1.0f));
    }

    [Test]
    public void LadderPolicy_GetEdgeInfo_IgnoresNodePositions(){
        // Cost is constant regardless of how far apart the nodes are placed.
        Node a = new Node(0f, 0f);
        Node b = new Node(99f, 99f);
        var (cost, length) = LadderPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(2.0f));
        Assert.That(length, Is.EqualTo(1.0f));
    }

    // ── CliffPolicy.GetEdgeInfo ────────────────────────────────────────
    [Test]
    public void CliffPolicy_GetEdgeInfo_ReturnsConstantCostAndLength(){
        Node a = new Node(2f, 4.25f);
        Node b = new Node(2f, 5.25f);
        var (cost, length) = CliffPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(3.0f));
        Assert.That(length, Is.EqualTo(1.0f));
    }

    // ── StairPolicy.GetEdgeInfo ────────────────────────────────────────
    [Test]
    public void StairPolicy_GetEdgeInfo_ReturnsConstantCostAndDiagonalLength(){
        Node a = new Node(0f, 0f);
        Node b = new Node(1f, 1f);
        var (cost, length) = StairPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(1.8f));
        // 1.4142 ≈ √2 — used as the physical diagonal length so locomotion timing matches geometry.
        Assert.That(length, Is.EqualTo(1.4142f));
    }

    // ── WaypointApproachPolicy.GetEdgeInfo ─────────────────────────────
    [Test]
    public void WaypointApproachPolicy_GetEdgeInfo_HorizontalUnit_ReturnsOne(){
        Node a = new Node(0f, 0f);
        Node b = new Node(1f, 0f);
        var (cost, length) = WaypointApproachPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(1f).Within(1e-5f));
        Assert.That(length, Is.EqualTo(1f).Within(1e-5f));
    }

    [Test]
    public void WaypointApproachPolicy_GetEdgeInfo_VerticalUnit_ReturnsOne(){
        Node a = new Node(0f, 0f);
        Node b = new Node(0f, 1f);
        var (cost, length) = WaypointApproachPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(1f).Within(1e-5f));
        Assert.That(length, Is.EqualTo(1f).Within(1e-5f));
    }

    [Test]
    public void WaypointApproachPolicy_GetEdgeInfo_Diagonal_ReturnsEuclidean(){
        Node a = new Node(0f, 0f);
        Node b = new Node(3f, 4f);
        var (cost, length) = WaypointApproachPolicy.Instance.GetEdgeInfo(a, b);
        // 3-4-5 triangle: dist = 5
        Assert.That(cost, Is.EqualTo(5f).Within(1e-5f));
        Assert.That(length, Is.EqualTo(5f).Within(1e-5f));
    }

    [Test]
    public void WaypointApproachPolicy_GetEdgeInfo_FractionalOffset_ReturnsEuclidean(){
        // Workspot waypoints can be authored at fractional offsets — the policy must
        // produce the right distance for those.
        Node a = new Node(0f, 0f);
        Node b = new Node(0.3f, 0.4f);
        var (cost, length) = WaypointApproachPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(length, Is.EqualTo(0.5f).Within(1e-5f));
    }

    [Test]
    public void WaypointApproachPolicy_GetEdgeInfo_NegativeOffset_ReturnsAbsoluteDistance(){
        // Reverse direction: same Euclidean distance.
        Node a = new Node(5f, 5f);
        Node b = new Node(2f, 1f);
        var (cost, length) = WaypointApproachPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(5f).Within(1e-5f));
        Assert.That(length, Is.EqualTo(5f).Within(1e-5f));
    }

    [Test]
    public void WaypointApproachPolicy_GetEdgeInfo_SamePoint_ReturnsZero(){
        // Edge case: from == to. The base contract says "don't return zero length"
        // but the policy itself doesn't guard — that's the caller's responsibility.
        // Documenting current behaviour.
        Node a = new Node(2f, 3f);
        Node b = new Node(2f, 3f);
        var (cost, length) = WaypointApproachPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(0f));
        Assert.That(length, Is.EqualTo(0f));
    }

    [Test]
    public void WaypointApproachPolicy_GetEdgeInfo_CostEqualsLength(){
        // Invariant for this policy: cost and length always match (both are the Euclidean dist).
        Node a = new Node(1f, 2f);
        Node b = new Node(7f, 9f);
        var (cost, length) = WaypointApproachPolicy.Instance.GetEdgeInfo(a, b);
        Assert.That(cost, Is.EqualTo(length));
    }

    // ── Default virtual flags ──────────────────────────────────────────
    [Test]
    public void PreventFall_AllConstantPolicies_DefaultTrue(){
        // Every current special edge suppresses falls; only plain horizontal edges
        // (which have no policy) fall through to false in Nav.MoveCore.
        Assert.That(LadderPolicy.Instance.PreventFall, Is.True);
        Assert.That(CliffPolicy.Instance.PreventFall, Is.True);
        Assert.That(StairPolicy.Instance.PreventFall, Is.True);
        Assert.That(WaypointApproachPolicy.Instance.PreventFall, Is.True);
    }

    [Test]
    public void SuspendsLerp_AllConstantPolicies_DefaultFalse(){
        // Only transit edges (Elevator) suspend lerp — constant policies all use Nav's
        // standard interpolation.
        Assert.That(LadderPolicy.Instance.SuspendsLerp, Is.False);
        Assert.That(CliffPolicy.Instance.SuspendsLerp, Is.False);
        Assert.That(StairPolicy.Instance.SuspendsLerp, Is.False);
        Assert.That(WaypointApproachPolicy.Instance.SuspendsLerp, Is.False);
    }

    // ── Default no-op hooks ────────────────────────────────────────────
    [Test]
    public void OnApproach_BaseDefault_DoesNotThrow(){
        // Constant policies don't override OnApproach — base no-op should accept null
        // animal/nodes without complaint (Nav guarantees real args at callsites).
        Assert.DoesNotThrow(() => LadderPolicy.Instance.OnApproach(null, null, null));
        Assert.DoesNotThrow(() => CliffPolicy.Instance.OnApproach(null, null, null));
        Assert.DoesNotThrow(() => StairPolicy.Instance.OnApproach(null, null, null));
        Assert.DoesNotThrow(() => WaypointApproachPolicy.Instance.OnApproach(null, null, null));
    }

    [Test]
    public void OnPathCommit_BaseDefault_DoesNotThrow(){
        Assert.DoesNotThrow(() => LadderPolicy.Instance.OnPathCommit(null));
        Assert.DoesNotThrow(() => CliffPolicy.Instance.OnPathCommit(null));
        Assert.DoesNotThrow(() => StairPolicy.Instance.OnPathCommit(null));
        Assert.DoesNotThrow(() => WaypointApproachPolicy.Instance.OnPathCommit(null));
    }

    [Test]
    public void OnPathRelease_BaseDefault_DoesNotThrow(){
        Assert.DoesNotThrow(() => LadderPolicy.Instance.OnPathRelease(null));
        Assert.DoesNotThrow(() => CliffPolicy.Instance.OnPathRelease(null));
        Assert.DoesNotThrow(() => StairPolicy.Instance.OnPathRelease(null));
        Assert.DoesNotThrow(() => WaypointApproachPolicy.Instance.OnPathRelease(null));
    }

    // ── Cost ratios (regression sanity) ────────────────────────────────
    [Test]
    public void CliffPolicy_IsSlowerThanLadder(){
        // Designed: cliff (3.0) is slower than ladder (2.0). Captures the intended
        // ordering so a tweak to the constants triggers a deliberate update.
        var (ladderCost, _) = LadderPolicy.Instance.GetEdgeInfo(new Node(0f, 0f), new Node(0f, 1f));
        var (cliffCost, _)  = CliffPolicy.Instance.GetEdgeInfo(new Node(0f, 0f), new Node(0f, 1f));
        Assert.That(cliffCost, Is.GreaterThan(ladderCost));
    }

    [Test]
    public void StairPolicy_LengthMatchesSqrtTwo(){
        // 1.4142 ≈ √2; sanity-check the constant matches the geometry it represents.
        var (_, length) = StairPolicy.Instance.GetEdgeInfo(new Node(0f, 0f), new Node(1f, 1f));
        Assert.That(length, Is.EqualTo(Mathf.Sqrt(2f)).Within(0.001f));
    }
}
