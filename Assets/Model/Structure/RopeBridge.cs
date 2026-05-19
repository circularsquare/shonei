using System.Collections.Generic;
using UnityEngine;

// Side-car entity linking two BridgePost structures. NOT a Structure itself —
// the two posts are normal 1×1 depth-2 structures owning their own
// placement, supply, construction, save data, and decay. RopeBridge owns
// only what spans BETWEEN them: a navigation waypoint chain edged into the
// Graph, and a visual LineRenderer drawing the rope curve.
//
// Lifecycle entry points:
//   - Live build: BridgePost.OnPlaced calls Create() once both posts exist.
//   - Load: SaveSystem calls PairAllAfterLoad() between Phase 3 and Phase 4
//     so the new waypoint edges enter Graph.Initialize's first
//     RebuildComponents sweep.
//   - Teardown: BridgePost.Destroy calls OnPostDestroyed(), which clears
//     edges, destroys visuals, AND destroys the partner post.
//
// Save format: RopeBridge state IS the pair of BridgePosts + their
// partnerX/partnerY fields. No separate top-level bridges[] list.
//
// Per-instance BridgePolicy: each bridge owns one BridgePolicy instance
// shared across its waypoints. Graph.IsNeighbor + Graph.ResolveEdgePolicy
// use shared-reference equality, so a singleton would falsely glue
// unrelated bridges' waypoints together across UpdateNeighbors rebuilds.
public class RopeBridge {

    public BridgePost postA;
    public BridgePost postB;
    public Node[] waypoints;
    public BridgePolicy policy;

    // Visual root + the LineRenderers drawing the rope curve. Parented to
    // StructController so they track the world lifecycle.
    // `line`         — walking rope mice traverse on top of.
    // `handropeLine` — secondary rope drawn above as the visual "handrail."
    // `connectorLines` — short vertical ropes joining walking line to handrope
    //                    at periodic t-values. Count is fixed per bridge at
    //                    BuildVisual time so RefreshLinePositions can update
    //                    in place without re-allocating GameObjects.
    GameObject visualGo;
    LineRenderer line;
    LineRenderer handropeLine;
    LineRenderer[] connectorLines;

    // Visual constants. HandropeHeight is the vertical offset between walking
    // line and handrope (in world units). ConnectorSpacing is the desired x
    // distance between connector verticals; actual count is clamped so even
    // short bridges get at least one connector.
    const float HandropeHeight   = 0.625f;
    const float ConnectorSpacing = 1.2f;

    // Registry of every live bridge, parallel to Elevator's static all-list.
    // Read by PairAllAfterLoad and (future) wind-physics tick.
    static List<RopeBridge> _all = new List<RopeBridge>();
    public static IReadOnlyList<RopeBridge> All => _all;

    // Reload-Domain-off support — Unity skips static reinit on Play, so the
    // list would otherwise leak entries from the editor session into the
    // first Play tick. See SpriteMaterialUtil for the broader pattern.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { _all = new List<RopeBridge>(); }

    // Material/colour cache for the rope LineRenderer. URP 2D Sprite-Unlit-
    // Default keeps the rope visible regardless of ambient light (mirrors the
    // Blueprint frame-overlay pattern). Phase 2 visuals (handrope + vertical
    // connectors) will likely upgrade to a lit material.
    static Material _ropeMaterial;
    static Material GetRopeMaterial() {
        if (_ropeMaterial != null) return _ropeMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (shader == null) {
            Debug.LogError("RopeBridge: URP Sprite-Unlit-Default shader not found — rope will render with default LineRenderer material");
            return null;
        }
        _ropeMaterial = new Material(shader) { name = "RopeBridgeUnlit" };
        return _ropeMaterial;
    }

    // Factory entry point. Refuses to double-create when either post already
    // belongs to a bridge — guards against the live-build path racing the
    // load-pairing path (the second never fires in practice, but the check
    // costs nothing).
    public static RopeBridge Create(BridgePost a, BridgePost b) {
        if (a == null || b == null) {
            Debug.LogError("RopeBridge.Create: null post");
            return null;
        }
        if (a.bridge != null || b.bridge != null) {
            Debug.LogError($"RopeBridge.Create: refusing to double-create at ({a.x},{a.y}) ↔ ({b.x},{b.y})");
            return null;
        }
        return new RopeBridge(a, b);
    }

    RopeBridge(BridgePost a, BridgePost b) {
        postA = a;
        postB = b;
        a.bridge = this;
        b.bridge = this;
        policy = new BridgePolicy();
        BuildWaypointChain();
        BuildVisual();
        _all.Add(this);
    }

    // Called from SaveSystem between Phase 3 and Phase 4. Iterates every
    // BridgePost in the world, finds its partner, and creates the bridge.
    // The first post of each pair to be visited creates the bridge; the
    // second finds bridge already set and skips.
    public static void PairAllAfterLoad() {
        if (StructController.instance == null) return;
        List<BridgePost> posts = new List<BridgePost>();
        foreach (Structure s in StructController.instance.GetStructures())
            if (s is BridgePost bp) posts.Add(bp);
        foreach (BridgePost bp in posts) {
            if (bp.bridge != null) continue;
            BridgePost partner = bp.FindPartner();
            if (partner == null) {
                Debug.LogWarning($"RopeBridge.PairAllAfterLoad: post at ({bp.x},{bp.y}) has no partner at ({bp.partnerX},{bp.partnerY}) — orphaned");
                continue;
            }
            Create(bp, partner);
        }
    }

    // ── Rope attachment geometry ──────────────────────────────────────
    // The post sprite is asymmetric: the pole sits on the LEFT half of the
    // un-mirrored sprite. The LEFT post of the bridge is mirrored at
    // construction so its pole sits on the RIGHT side of its tile, facing the
    // bridge. The RIGHT post stays un-mirrored so its pole faces left, also
    // toward the bridge. The rope then attaches at each pole's BASE — pulled
    // 0.125 PAST the tile edge into the post so the rope visually "grips"
    // the pole rather than floating off the corner:
    //   - Left post (mirrored):  (xL + 0.375, yL - 0.5)  — 0.125 left of tile's right edge
    //   - Right post (un-mirr.): (xR - 0.375, yR - 0.5)  — 0.125 right of tile's left edge
    // These attachment points are the catenary's world-coord endpoints — used
    // for the rope LineRenderer, the handrope, the connector verticals, AND
    // the nav waypoint chain (corner waypoints sit just above them). Both
    // visual and walking line follow the same catenary so the mouse never
    // appears to walk off the rope.
    const float AttachmentInset = 0.375f;
    void AttachmentPoints(out float wxLeft, out float wyLeft, out float wxRight, out float wyRight) {
        BridgePost leftPost  = postA.x <= postB.x ? postA : postB;
        BridgePost rightPost = postA.x <= postB.x ? postB : postA;
        wxLeft  = leftPost.x  + AttachmentInset;
        wyLeft  = leftPost.y  - 0.5f;
        wxRight = rightPost.x - AttachmentInset;
        wyRight = rightPost.y - 0.5f;
    }

    // Per-instance offset between the rope visual and the mouse's nav-y. The
    // mouse walks ON TOP of the rope rather than through it: waypoint.wy is
    // catenary_y + WalkAboveRope so the rope renders at foot level.
    const float WalkAboveRope = 0.5f;

    // ── Waypoint chain ────────────────────────────────────────────────
    // Builds N interior waypoint Nodes along the catenary and edges them:
    // post_lo.tileNode ↔ wp[0] ↔ wp[1] ↔ ... ↔ wp[N-1] ↔ post_hi.tileNode.
    //
    // Each interior waypoint carries the per-instance `policy` ref. The
    // approach edges (waypoint ↔ post tile-node) deliberately do NOT — they
    // resolve to WaypointApproachPolicy.Instance via Graph.ResolveEdgePolicy's
    // waypoint-flag fallback, which already gives euclidean cost.
    //
    // Endpoint convergence: at the rope's left end, catenary_y = wyLeft = yL - 0.5,
    // so the first waypoint sits at wy = (yL - 0.5) + WalkAboveRope = yL when
    // WalkAboveRope == 0.5 — i.e. exactly the post tile-node's wy, so the
    // mouse transitions smoothly between the post tile and the chain. Same
    // logic on the right end.
    //
    // No standability gate on the post tile-nodes: at load time this runs
    // BEFORE graph.Initialize's standability pass (Phase 4), so a gate
    // would falsely reject every load-time edge creation. Placement-time
    // validation guarantees the post tiles are standable.
    void BuildWaypointChain() {
        float sagFraction = postA.structType.sagFraction;
        if (sagFraction <= 0f) sagFraction = 0.15f;
        AttachmentPoints(out float wxL, out float wyL, out float wxR, out float wyR);
        Vector2[] interior = Catenary.WaypointPositions(wxL, wyL, wxR, wyR, sagFraction);

        // Layout: leftTile ↔ leftCorner ↔ wp[0] ↔ ... ↔ wp[N-1] ↔ rightCorner ↔ rightTile
        //
        // The anchor-corner waypoints sit at the tile edge above the rope start
        // (x = wxL or wxR; y = post.y, i.e. catenary_y + WalkAboveRope at the
        // rope endpoint, which equals the post tile-node's wy exactly). Without
        // them the chain would step diagonally from post tile-centre straight
        // to the first interior catenary sample — the mouse walks through the
        // post's silhouette before the rope descent. The corner waypoint forces
        // the path to be horizontal-then-descend, matching the visual rope.
        //
        // All bridge-owned waypoints carry the per-instance `policy` reference
        // so IsNeighbor + ResolveEdgePolicy keep the chain intact across
        // UpdateNeighbors rebuilds and the edges resolve to BridgePolicy
        // (euclidean cost, PreventFall = true).
        BridgePost leftPost  = postA.x <= postB.x ? postA : postB;
        BridgePost rightPost = postA.x <= postB.x ? postB : postA;
        Node leftCorner  = new Node(wxL, leftPost.y);
        Node rightCorner = new Node(wxR, rightPost.y);
        leftCorner.edgePolicy  = policy;
        rightCorner.edgePolicy = policy;

        // Full chain: corner + interior + corner. Index 0 = leftCorner, last = rightCorner.
        waypoints = new Node[interior.Length + 2];
        waypoints[0] = leftCorner;
        for (int i = 0; i < interior.Length; i++) {
            Node n = new Node(interior[i].x, interior[i].y + WalkAboveRope);
            n.edgePolicy = policy;
            waypoints[i + 1] = n;
        }
        waypoints[waypoints.Length - 1] = rightCorner;
        for (int i = 0; i < waypoints.Length - 1; i++)
            waypoints[i].AddNeighbor(waypoints[i + 1], reciprocal: true);

        // Edge the two corners to their respective post tile-nodes. No
        // standability gate: at load time this runs BEFORE graph.Initialize's
        // standability pass (Phase 4), so a gate would falsely reject every
        // load-time edge creation. Placement-time validation already
        // guarantees the post tiles are standable.
        Graph g = World.instance.graph;
        Node leftTileNode  = g.nodes[leftPost.x,  leftPost.y];
        Node rightTileNode = g.nodes[rightPost.x, rightPost.y];
        leftTileNode.AddNeighbor(leftCorner,   reciprocal: true);
        rightTileNode.AddNeighbor(rightCorner, reciprocal: true);
    }

    // ── Visual ────────────────────────────────────────────────────────
    // Three layers compose the bridge:
    //   - Walking line (`line`): the catenary mice traverse on top of.
    //   - Handrope (`handropeLine`): same curve offset upward by HandropeHeight.
    //   - Connectors (`connectorLines`): short verticals joining the two ropes
    //     at periodic t-values for the "many small ropes" suspension-bridge
    //     look.
    // All three sort behind the post sprites (sortingOrder=39 < depth-2's
    // default ~40) so the posts punch through visually at their attachments.
    void BuildVisual() {
        visualGo = new GameObject($"ropebridge_{postA.x}_{postA.y}_{postB.x}_{postB.y}");
        visualGo.transform.SetParent(StructController.instance.transform, true);

        line         = CreateRopeLine("walking_rope", 0.12f);
        handropeLine = CreateRopeLine("handrope",     0.08f);

        // Connector count is fixed at construction time so RefreshLinePositions
        // can update in place. Always ≥ 1 even for the shortest valid bridge.
        AttachmentPoints(out float wxL, out _, out float wxR, out _);
        float worldDx = wxR - wxL;
        int connectorCount = Mathf.Max(1, Mathf.FloorToInt(worldDx / ConnectorSpacing));
        connectorLines = new LineRenderer[connectorCount];
        for (int i = 0; i < connectorCount; i++) {
            connectorLines[i] = CreateRopeLine($"connector_{i}", 0.05f);
            connectorLines[i].positionCount = 2;
        }

        RefreshLinePositions();
    }

    // Helper: spawns one LineRenderer child of visualGo, configured with the
    // shared rope material + brown colour + correct sortingOrder. Caller sets
    // positionCount + SetPositions afterward.
    LineRenderer CreateRopeLine(string name, float width) {
        GameObject g = new GameObject(name);
        g.transform.SetParent(visualGo.transform, false);
        LineRenderer lr = g.AddComponent<LineRenderer>();
        lr.useWorldSpace      = true;
        lr.startWidth         = width;
        lr.endWidth           = width;
        lr.numCornerVertices  = 1;
        lr.numCapVertices     = 1;
        lr.sortingOrder       = 39;
        Material mat = GetRopeMaterial();
        if (mat != null) lr.sharedMaterial = mat;
        Color rope = new Color(0.55f, 0.35f, 0.18f);  // warm rope brown
        lr.startColor = rope;
        lr.endColor   = rope;
        return lr;
    }

    // Repopulates all three rope LineRenderers' positions from the current
    // catenary. Uses the same world-coord attachment points as BuildWaypointChain
    // so visuals coincide with the path mice walk minus WalkAboveRope (the
    // walking line sits at the mouse's foot height).
    // Called once in BuildVisual; future wind physics will re-call every tick
    // — connector and rope GO counts are fixed so this allocates only the
    // sample arrays (kept small).
    void RefreshLinePositions() {
        float sagFraction = postA.structType.sagFraction;
        if (sagFraction <= 0f) sagFraction = 0.15f;
        AttachmentPoints(out float wxL, out float wyL, out float wxR, out float wyR);
        float worldDx = wxR - wxL;

        // ~4 rope segments per world unit + 1 closing point. Smooth enough at
        // typical zoom; cheap. Walking line and handrope share the sample
        // count — handrope is just walking + (0, HandropeHeight, 0).
        int samples = Mathf.Max(8, Mathf.CeilToInt(worldDx * 4f));
        Vector3[] walkPoints = new Vector3[samples + 1];
        Vector3[] handPoints = new Vector3[samples + 1];
        for (int i = 0; i <= samples; i++) {
            float t = i / (float)samples;
            float x = wxL + t * worldDx;
            float y = Catenary.YAt(wxL, wyL, wxR, wyR, sagFraction, x);
            walkPoints[i] = new Vector3(x, y, 0);
            handPoints[i] = new Vector3(x, y + HandropeHeight, 0);
        }
        line.positionCount = walkPoints.Length;
        line.SetPositions(walkPoints);
        handropeLine.positionCount = handPoints.Length;
        handropeLine.SetPositions(handPoints);

        // Vertical connectors. Distribute uniformly across the interior of the
        // bridge (t = 1/(N+1), 2/(N+1), ..., N/(N+1) — neither connector lands
        // exactly on a post).
        for (int i = 0; i < connectorLines.Length; i++) {
            float t = (i + 1f) / (connectorLines.Length + 1f);
            float x = wxL + t * worldDx;
            float y = Catenary.YAt(wxL, wyL, wxR, wyR, sagFraction, x);
            connectorLines[i].SetPosition(0, new Vector3(x, y,                 0));
            connectorLines[i].SetPosition(1, new Vector3(x, y + HandropeHeight, 0));
        }
    }

    // ── Teardown ──────────────────────────────────────────────────────
    // Called from BridgePost.Destroy on the post being mined / deconstructed.
    // Order is load-bearing: we MUST null both posts' `bridge` refs BEFORE
    // calling Destroy on the partner — otherwise partner.Destroy would see
    // bridge != null and re-enter this branch, recursing forever.
    public void OnPostDestroyed(BridgePost destroyingPost) {
        BridgePost partner = (destroyingPost == postA) ? postB : postA;

        // Detach back-refs first so the cascading Destroy below doesn't
        // re-enter this method.
        postA.bridge = null;
        postB.bridge = null;

        TeardownChain();
        TeardownVisual();
        _all.Remove(this);

        // Cascade: the surviving post is structurally meaningless without
        // its partner (just a stake in the ground), so we destroy it. The
        // `destroyingPost` is being torn down by its own Destroy() — we
        // don't touch that one; control returns to its base.Destroy().
        if (partner != null && partner != destroyingPost)
            partner.Destroy();
    }

    // Pull every waypoint out of the Graph: drop its neighbors' reciprocal
    // back-edges, then clear its own neighbor list. Matches the Structure
    // workspot-waypoint teardown pattern in Structure.Destroy.
    void TeardownChain() {
        if (waypoints == null) return;
        for (int i = 0; i < waypoints.Length; i++) {
            Node wp = waypoints[i];
            if (wp == null) continue;
            foreach (Node n in wp.neighbors) n.RemoveNeighbor(wp);
            wp.neighbors.Clear();
        }
        waypoints = null;
    }

    void TeardownVisual() {
        // GameObject.Destroy on visualGo cascades to every child — walking line,
        // handrope, connectors — so no per-child cleanup loop needed.
        if (visualGo != null) GameObject.Destroy(visualGo);
        visualGo       = null;
        line           = null;
        handropeLine   = null;
        connectorLines = null;
    }
}
