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

    // Visual root + sprite chains. Replaces an earlier LineRenderer-based
    // implementation that drew the rope as smooth antialiased lines — wrong
    // for pixel art, no normal maps, sub-pixel widths. Each piece below is a
    // regular SpriteRenderer so it participates in the project's lighting +
    // normal map pipeline automatically.
    //
    // walkingPlanks — short log / plank sprites along the walking catenary,
    //                 rotated to the local tangent. Mice walk on top.
    // walkingRopes  — short rope-segment sprites between consecutive planks,
    //                 rotated + scaled to fill each gap.
    // handropeRopes — rope-segment sprites along the upper curve. No planks
    //                 (handrope is just a held-onto rope, not a walking surface).
    // connectorRopes — vertical rope sprites at every Kth plank, linking
    //                 walking line to handrope.
    GameObject visualGo;
    GameObject[] walkingPlanks;
    GameObject[] walkingRopes;
    GameObject[] handropeRopes;
    GameObject[] connectorRopes;

    // Visual tuning. HandropeHeight is the vertical offset between walking
    // line and handrope. PlankSpacing is target distance between plank centres
    // (also drives handrope sample density). ConnectorEveryNPlanks is how many
    // planks separate consecutive connector verticals.
    const float HandropeHeight       = 0.625f;
    const float PlankSpacing         = 0.5f;
    const int   ConnectorEveryNPlanks = 3;

    // Sprite cache. Loaded once per process; null entries log a warning
    // when missing and the relevant chain is skipped (lets the user
    // iterate sprite art without breaking the rest of the bridge).
    // bridgeropev is optional — if absent, connectors fall back to the
    // horizontal rope sprite rotated 90°.
    static Sprite _plankSprite;
    static Sprite _ropeSprite;
    static Sprite _ropeVSprite;
    static bool   _spritesProbed;
    static void LoadSpritesOnce() {
        if (_spritesProbed) return;
        _plankSprite = Resources.Load<Sprite>("Sprites/Buildings/bridgeplank");
        _ropeSprite  = Resources.Load<Sprite>("Sprites/Buildings/bridgerope");
        _ropeVSprite = Resources.Load<Sprite>("Sprites/Buildings/bridgeropev");
        if (_plankSprite == null) Debug.LogWarning("RopeBridge: missing Resources/Sprites/Buildings/bridgeplank.png — walking-line planks will not render");
        if (_ropeSprite  == null) Debug.LogWarning("RopeBridge: missing Resources/Sprites/Buildings/bridgerope.png — rope segments will not render");
        // _ropeVSprite is optional; no warning if missing.
        _spritesProbed = true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetSpriteCache() {
        _spritesProbed = false;
        _plankSprite   = null;
        _ropeSprite    = null;
        _ropeVSprite   = null;
    }

    // Registry of every live bridge, parallel to Elevator's static all-list.
    // Read by PairAllAfterLoad and (future) wind-physics tick.
    static List<RopeBridge> _all = new List<RopeBridge>();
    public static IReadOnlyList<RopeBridge> All => _all;

    // Reload-Domain-off support — Unity skips static reinit on Play, so the
    // list would otherwise leak entries from the editor session into the
    // first Play tick. See SpriteMaterialUtil for the broader pattern.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { _all = new List<RopeBridge>(); }


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
        // Register so RebuildComponents resets these off-grid waypoints' componentId each rebuild.
        for (int i = 0; i < waypoints.Length; i++)
            World.instance.graph.RegisterWaypoint(waypoints[i]);
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
    // Sprite-chain composition. The bridge has three parallel chains, all
    // sampled at the same x positions ("plank centres") so connectors line up
    // with planks like a real suspension bridge:
    //
    //   walking line:  plank → rope → plank → rope → … → plank
    //                  (planks at sample positions, rotated to tangent;
    //                   ropes in gaps, rotated + scaled to fill)
    //   handrope:      same x samples but plain rope segments end-to-end,
    //                  offset by (0, HandropeHeight). No planks.
    //   connectors:    vertical rope sprite at every ConnectorEveryNPlanks'th
    //                  plank, length = HandropeHeight.
    //
    // All sort at order 39 — behind posts (~40 for depth-2 default) so the
    // posts visually punch through where the rope attaches.
    //
    // Each piece is a SpriteRenderer added via SpriteMaterialUtil so it
    // participates in the project's lighting + auto-normal-map pipeline.
    // GameObject arrays are stored so future wind physics can move individual
    // planks without rebuilding the whole chain.
    void BuildVisual() {
        LoadSpritesOnce();

        visualGo = new GameObject($"ropebridge_{postA.x}_{postA.y}_{postB.x}_{postB.y}");
        visualGo.transform.SetParent(StructController.instance.transform, true);

        float sagFraction = postA.structType.sagFraction;
        if (sagFraction <= 0f) sagFraction = 0.15f;
        AttachmentPoints(out float wxL, out float wyL, out float wxR, out float wyR);
        float worldDx = wxR - wxL;

        // Sample positions along the catenary. nPlanks ≥ 2 covers both endpoints.
        int nPlanks = Mathf.Max(2, Mathf.RoundToInt(worldDx / PlankSpacing) + 1);
        Vector2[] pts = new Vector2[nPlanks];
        for (int i = 0; i < nPlanks; i++) {
            float t = i / (float)(nPlanks - 1);
            float x = wxL + t * worldDx;
            float y = Catenary.YAt(wxL, wyL, wxR, wyR, sagFraction, x);
            pts[i] = new Vector2(x, y);
        }

        // Sort layering: planks (39) > horizontal ropes (38) > vertical
        // connectors (37). Posts sit at depth-2 default (~40) above
        // everything, so they still punch through the rope at attachments.
        const int OrderPlank     = 39;
        const int OrderHorizRope = 38;
        const int OrderVertRope  = 37;

        // Walking line — planks at samples + rope segments between. Planks
        // are NOT rotated; they stay upright regardless of catenary slope.
        // Endpoint planks (i=0, i=last) are skipped — those samples sit
        // under the post sprites where a plank would visually clutter the
        // pole base. The rope still spans those gaps to attach to the pole.
        GameObject walkingRoot = MakeChainParent("walking");
        walkingPlanks = new GameObject[nPlanks];
        walkingRopes  = new GameObject[nPlanks - 1];
        for (int i = 1; i < nPlanks - 1; i++) {
            walkingPlanks[i] = SpawnSprite(walkingRoot, "plank_" + i, _plankSprite,
                                           position: pts[i], angleDeg: 0f,
                                           sortingOrder: OrderPlank);
        }
        for (int i = 0; i < nPlanks - 1; i++) {
            walkingRopes[i] = SpawnRopeSegment(walkingRoot, "wrope_" + i, pts[i], pts[i + 1], OrderHorizRope);
        }

        // Handrope — rope segments only, same x samples shifted up.
        GameObject handropeRoot = MakeChainParent("handrope");
        handropeRopes = new GameObject[nPlanks - 1];
        for (int i = 0; i < nPlanks - 1; i++) {
            Vector2 a = pts[i]     + new Vector2(0f, HandropeHeight);
            Vector2 b = pts[i + 1] + new Vector2(0f, HandropeHeight);
            handropeRopes[i] = SpawnRopeSegment(handropeRoot, "hrope_" + i, a, b, OrderHorizRope);
        }

        // Vertical connectors — every Kth plank. Sorted BENEATH horizontal
        // ropes so the handrope/walking ropes visually pass in front where
        // they meet a connector.
        GameObject connectorRoot = MakeChainParent("connectors");
        int connectorCount = 0;
        for (int i = 0; i < nPlanks; i += ConnectorEveryNPlanks) connectorCount++;
        connectorRopes = new GameObject[connectorCount];
        int ci = 0;
        Sprite verticalSprite = _ropeVSprite != null ? _ropeVSprite : _ropeSprite;
        for (int i = 0; i < nPlanks; i += ConnectorEveryNPlanks) {
            Vector2 bottom = pts[i];
            Vector2 top    = pts[i] + new Vector2(0f, HandropeHeight);
            connectorRopes[ci++] = SpawnConnector(connectorRoot, "conn_" + ci, bottom, top, verticalSprite, OrderVertRope);
        }
    }

    GameObject MakeChainParent(string name) {
        GameObject g = new GameObject(name);
        g.transform.SetParent(visualGo.transform, false);
        return g;
    }

    // Spawns a single SR child. Sprite may be null — in which case the GO
    // exists but renders nothing (preserves array slot for future wind hooks).
    GameObject SpawnSprite(GameObject parent, string name, Sprite sprite, Vector2 position, float angleDeg, int sortingOrder) {
        GameObject g = new GameObject(name);
        g.transform.SetParent(parent.transform, false);
        g.transform.position = new Vector3(position.x, position.y, 0f);
        g.transform.rotation = Quaternion.Euler(0f, 0f, angleDeg);
        SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(g);
        sr.sprite       = sprite;
        sr.sortingOrder = sortingOrder;
        LightReceiverUtil.SetSortBucket(sr);
        return g;
    }

    // Rope segment between two endpoints. Rotates the rope sprite so its
    // length runs along (a → b) and scales localScale.x so the sprite's
    // native width covers the gap. Pixel art note: small non-unit scales
    // introduce sub-pixel sampling; keep PlankSpacing close to the rope
    // sprite's native width to minimise this.
    GameObject SpawnRopeSegment(GameObject parent, string name, Vector2 a, Vector2 b, int sortingOrder) {
        Vector2 mid = (a + b) * 0.5f;
        Vector2 dir = b - a;
        float length   = dir.magnitude;
        float angleDeg = (length > 0f) ? Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg : 0f;
        GameObject g = SpawnSprite(parent, name, _ropeSprite, mid, angleDeg, sortingOrder);
        if (_ropeSprite != null) {
            float spriteWidth = _ropeSprite.bounds.size.x;
            if (spriteWidth > 0f)
                g.transform.localScale = new Vector3(length / spriteWidth, 1f, 1f);
        }
        return g;
    }

    // Vertical connector. Always exactly vertical (handrope is parallel to
    // walking line), so we can use the dedicated vertical rope sprite when
    // available and only y-scale to match HandropeHeight.
    GameObject SpawnConnector(GameObject parent, string name, Vector2 bottom, Vector2 top, Sprite spr, int sortingOrder) {
        Vector2 mid = (bottom + top) * 0.5f;
        // If we fell back to the horizontal rope, rotate 90°. The dedicated
        // vertical sprite renders un-rotated.
        float angleDeg = (spr == _ropeVSprite) ? 0f : 90f;
        GameObject g = SpawnSprite(parent, name, spr, mid, angleDeg, sortingOrder);
        if (spr != null) {
            // The connector's "length" axis is the sprite's vertical axis when
            // un-rotated; we scale that axis to match HandropeHeight.
            float spriteHeight = (spr == _ropeVSprite) ? spr.bounds.size.y : spr.bounds.size.x;
            if (spriteHeight > 0f) {
                float scaleY = HandropeHeight / spriteHeight;
                // Rotated horizontal-rope path uses x-scale because the sprite's
                // "long axis" became vertical after the 90° rotation but local
                // axes don't follow rotation in Unity — scale is applied before
                // rotation. So scaleY there really means scale of the sprite's
                // original x.
                g.transform.localScale = (spr == _ropeVSprite)
                    ? new Vector3(1f, scaleY, 1f)
                    : new Vector3(scaleY, 1f, 1f);
            }
        }
        return g;
    }

    // ── Preview (ghost catenary during second-click hover) ───────────
    // Builds a translucent walking-line preview between two tile endpoints
    // — planks + ropes only, no handrope or connectors. Tinted with the
    // standard blueprint blue so it reads as "this would be placed here."
    // Called per cursor-move by MouseController.UpdateGhostCatenary.
    //
    // Static (not instance) because preview lives BEFORE a RopeBridge
    // entity exists. Endpoint planks are skipped to match the production
    // build's "nothing under the poles" rule.
    public static void BuildPreviewChain(GameObject root, StructType st, int xA, int yA, int xB, int yB, int sortingOrder) {
        LoadSpritesOnce();
        if (_plankSprite == null && _ropeSprite == null) return;

        float sagFraction = st.sagFraction > 0f ? st.sagFraction : 0.15f;

        bool aIsLeft = xA <= xB;
        int leftX  = aIsLeft ? xA : xB;
        int leftY  = aIsLeft ? yA : yB;
        int rightX = aIsLeft ? xB : xA;
        int rightY = aIsLeft ? yB : yA;
        float wxL = leftX  + AttachmentInset;
        float wyL = leftY  - 0.5f;
        float wxR = rightX - AttachmentInset;
        float wyR = rightY - 0.5f;
        float worldDx = wxR - wxL;
        if (worldDx < 0.1f) return;

        int nPlanks = Mathf.Max(2, Mathf.RoundToInt(worldDx / PlankSpacing) + 1);
        Vector2[] pts = new Vector2[nPlanks];
        for (int i = 0; i < nPlanks; i++) {
            float t = i / (float)(nPlanks - 1);
            float x = wxL + t * worldDx;
            float y = Catenary.YAt(wxL, wyL, wxR, wyR, sagFraction, x);
            pts[i] = new Vector2(x, y);
        }

        Color tint = new Color(0.8f, 0.9f, 1f, 0.5f);
        // Planks above ropes in the preview's local stack — matches the production layering.
        for (int i = 1; i < nPlanks - 1; i++)
            SpawnPreviewSprite(root, "preview_plank_" + i, _plankSprite, pts[i], 0f, sortingOrder + 1, tint);
        for (int i = 0; i < nPlanks - 1; i++)
            SpawnPreviewRope(root, "preview_wrope_" + i, pts[i], pts[i + 1], sortingOrder, tint);
    }

    static GameObject SpawnPreviewSprite(GameObject parent, string name, Sprite sprite, Vector2 pos, float angleDeg, int sortingOrder, Color tint) {
        if (sprite == null) return null;
        GameObject g = new GameObject(name);
        g.transform.SetParent(parent.transform, false);
        g.transform.position = new Vector3(pos.x, pos.y, 0f);
        g.transform.rotation = Quaternion.Euler(0f, 0f, angleDeg);
        SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(g);
        sr.sprite       = sprite;
        sr.color        = tint;
        sr.sortingOrder = sortingOrder;
        LightReceiverUtil.SetSortBucket(sr);
        return g;
    }

    static GameObject SpawnPreviewRope(GameObject parent, string name, Vector2 a, Vector2 b, int sortingOrder, Color tint) {
        if (_ropeSprite == null) return null;
        Vector2 mid = (a + b) * 0.5f;
        Vector2 dir = b - a;
        float length   = dir.magnitude;
        float angleDeg = (length > 0f) ? Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg : 0f;
        GameObject g = SpawnPreviewSprite(parent, name, _ropeSprite, mid, angleDeg, sortingOrder, tint);
        if (g != null) {
            float spriteWidth = _ropeSprite.bounds.size.x;
            if (spriteWidth > 0f)
                g.transform.localScale = new Vector3(length / spriteWidth, 1f, 1f);
        }
        return g;
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
            World.instance.graph.UnregisterWaypoint(wp);
            foreach (Node n in wp.neighbors) n.RemoveNeighbor(wp);
            wp.neighbors.Clear();
        }
        waypoints = null;
    }

    void TeardownVisual() {
        // GameObject.Destroy on visualGo cascades to every child — walking
        // planks/ropes, handrope segments, connectors — so no per-child
        // cleanup loop needed.
        if (visualGo != null) GameObject.Destroy(visualGo);
        visualGo       = null;
        walkingPlanks  = null;
        walkingRopes   = null;
        handropeRopes  = null;
        connectorRopes = null;
    }
}
