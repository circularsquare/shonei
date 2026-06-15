using UnityEngine;

// DiggingPit digs out the substrate it was built on (dirt, sand, or clay).
// Capture/extraction mechanics live in the ExtractionBuilding base; this class
// adds the dig-direction door wiring and the receding-dish visuals. The earth
// tiles' distributions (1 liang substrate per craft + alluvial clay nodules on
// dirt/sand + a bootstrap limestone nodule on dirt — the early, tool-free stone
// source) are authored in tilesDb.json nExtractionProducts.
//
// ── Dig direction ───────────────────────────────────────────────────────
// The pit digs toward whichever orthogonally-adjacent tile is OPEN, so it works
// in horizontal tunnels, not just from the surface. The direction (Up / Left /
// Right) is chosen ONCE at construction (ChooseDigDirection) from neighbour
// openness + reachability from the main settlement, then persisted in the save
// and never recomputed (a loaded pit reads digDir back verbatim). The dish bite,
// the worker's stand point, and the single wired door all orient to digDir.
//
// ── Dish visual ─────────────────────────────────────────────────────────
// The pit renders in two layers: the platform sprite (yellow frame, from the
// standard StructureVisualBuilder path) plus a dynamically generated "earth
// dish" sprite rendered in front. The dish is a 20×20 texture built from the
// captured tile's interior pixels (cardinal mask 15) with a parabolic mask
// carved out — thick at the rim, thinnest (the concave bite) toward the open
// face — so it shows the REMAINING substrate as a partially excavated bowl. As
// uses accumulates the remaining substrate recedes away from the opening, and
// the workspot tracks it so the digger appears to stand on the receding floor.
// On depletion the pit is replaced by a regular platform (see
// AnimalStateManager.HandleWorking).
//
// The cell's own tile body is suppressed (Tile.bodyRenderSuppressed) while the
// pit operates, so the carved-away region is a real open hole — the background
// wall, or sky where there is none, shows through the dish's transparent area.
// `preservesTile` is deliberately kept ON: the tile stays solid so NEIGHBOURING
// tiles still light as if the pit were solid earth. Only this cell's own body
// is hidden, replaced by the dish.
//
// The dish's normal map is re-baked from the carved silhouette every time the
// excavation deepens (TileSpriteCache.BakeMaskedNormalMap), so the receding bowl
// is lit as a freshly-cut surface (rim beveled, depths darkened) rather than a
// flat full tile.
// Open face the pit digs toward. Up == 0 so old saves (field absent) default to
// the original dig-from-the-top behaviour.
public enum DigDir { Up = 0, Left = 1, Right = 2 }

public class DiggingPit : ExtractionBuilding {
    // Chosen once at construction (live) or restored from save (load); never
    // recomputed. Orients the dish bite, the workspot, and the single wired door.
    public DigDir digDir = DigDir.Up;

    // Dish texture geometry, shared by the carve (UpdateDishTexture) and the
    // workspot tracking (UpdateWorkSpot).
    const int SIZE   = 20;  // matches TileSpriteCache.SIZE
    const int BORDER = 2;   // 2px overhang on each side of the 20px sprite
    const int INNER  = 16;  // 16×16 interior
    const int DEPTH  = 3;   // constant rim-minus-bite thickness, in pixels

    // Dish layer: separate SR rendered in front of the platform. Owned and
    // torn down by this class so the texture allocation doesn't leak.
    SpriteRenderer dishSr;
    Texture2D dishTex;
    Texture2D dishNormalTex; // carve-matched normal map; re-baked when uses changes, owned here
    Sprite dishSprite;
    Color32[] substratePixels;
    int lastDishUses = -1; // skip texture rebuilds when uses hasn't changed

    // The pit wires exactly one door — the chosen dig face — itself (the JSON
    // declares none), so reachability can be measured on the clean pre-pit graph
    // before the door bridges anything. Idempotent via this flag (OnPlaced and the
    // per-craft RebuildDishVisual both call EnsurePrimaryDoor).
    bool primaryDoorWired;

    // UP-dug pits only: once the bowl is mined past 60%, also open the L/R faces so
    // mice can hop in from the sides (the dirt's no longer in the way). Side-dug
    // pits use their single chosen door exclusively. Wired once on crossing 60%.
    bool sideDoorsWired;

    public DiggingPit(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    public override void OnPlaced() {
        base.OnPlaced();
        ChooseDigDirection();   // live path only; the load path restores digDir from save instead
        RebuildDishVisual();
    }

    // Picks the dig direction from the neighbours' openness and reachability. Runs
    // on the LIVE build path (OnPlaced), where the graph is still in its clean
    // pre-pit state (this pit hasn't wired its door yet, so no side is bridged
    // through it) and the component ids are current from the last RebuildComponents.
    //
    // A face is a candidate if its neighbour is OPEN (non-solid) and its approach
    // node is standable. Among candidates we prefer one reachable from the main
    // settlement (the nav component most mice occupy) so the pit doesn't end up
    // mineable only from a sealed cave pocket. Priority: up > a single side > both
    // sides (cosmetic left-tiebreak — both are reachable, so either works).
    void ChooseDigDirection() {
        World w = World.instance;
        int settlement = w.MainSettlementComponent(forceFresh: true); // -1 = unknown (no mice yet)

        bool upOpen    = IsOpenStandable(w, x, y + 1, out Node upN);
        bool leftOpen  = IsOpenStandable(w, x - 1, y, out Node leftN);
        bool rightOpen = IsOpenStandable(w, x + 1, y, out Node rightN);

        // When the settlement is unknown, treat every open+standable face as
        // acceptable (we can't tell — fall back to plain priority).
        bool upReach    = upOpen    && Reachable(upN,    settlement);
        bool leftReach  = leftOpen  && Reachable(leftN,  settlement);
        bool rightReach = rightOpen && Reachable(rightN, settlement);

        if (upReach)                       digDir = DigDir.Up;
        else if (leftReach && rightReach)  digDir = DigDir.Left;   // both work; pick one
        else if (leftReach)                digDir = DigDir.Left;
        else if (rightReach)               digDir = DigDir.Right;
        else if (upOpen || leftOpen || rightOpen) {
            // An open face exists but none reaches the settlement — keep the dish
            // visually coherent (faces a real opening) but warn: no mouse may reach it.
            digDir = upOpen ? DigDir.Up : leftOpen ? DigDir.Left : DigDir.Right;
            Debug.LogWarning($"DiggingPit at {x},{y}: no open face reachable from the settlement; mining may be impossible.");
        } else {
            // Fully enclosed in solid — degenerate. Dish faces up by convention.
            digDir = DigDir.Up;
            Debug.LogError($"DiggingPit at {x},{y}: built fully enclosed (no open face); cannot dig.");
        }
    }

    bool Reachable(Node n, int settlement) {
        if (settlement < 0) return true;            // settlement unknown → accept any open face
        return n != null && n.componentId == settlement;
    }

    // A face is diggable-toward if the neighbour tile is open (non-solid) and its
    // node is standable (a mouse can occupy it to mine from there).
    static bool IsOpenStandable(World w, int tx, int ty, out Node n) {
        n = null;
        Tile t = w.GetTileAt(tx, ty);
        if (t == null || t.type.solid) return false;
        n = t.node;
        return n != null && n.standable;
    }

    // Called from AnimalStateManager after each completed craft round, and from
    // RestoreOnLoad once digDir + capturedTile are restored.
    public void RebuildDishVisual() {
        if (capturedTile == null) return;       // nothing to draw yet (pre-capture or load mid-restore)
        if (go == null) return;                  // structure has been torn down
        EnsureDishObject();
        EnsurePrimaryDoor();
        UpdateDishTexture();
        UpdateWorkSpot();
        UpdateSideAccess();
        UpdateHoleLighting();
    }

    // Load entry point. OnPlaced is skipped on the load path, so SaveSystem calls
    // this — after restoring capturedTile via the shared ExtractionBuilding branch —
    // to restore the persisted direction, wire the single door (graph topology —
    // independent of capturedTile, so a pit whose substrate failed to resolve is
    // still reachable rather than orphaned), and rebuild the dish if it can.
    public void RestoreOnLoad(DigDir dir) {
        digDir = dir;
        EnsurePrimaryDoor();
        if (capturedTile != null) RebuildDishVisual();
    }

    // Wires the single door on the chosen dig face (the pit's own tile is the door
    // tile; WireDoorEdge resolves the approach). Idempotent. Needs only digDir and
    // workNode (the interior node, repointed by the Structure ctor) — no GameObject,
    // so it's safe on the load path before the dish renders.
    void EnsurePrimaryDoor() {
        if (primaryDoorWired || workNode == null) return;
        string side = digDir switch {
            DigDir.Left  => "left",
            DigDir.Right => "right",
            _            => "top",
        };
        WireDoorEdge(workNode, x, y, side);
        primaryDoorWired = true;
    }

    // Once the pit is dug past ~10%, flag the cell so neighbours light their
    // facing edges as exposed cliffs instead of buried seams (Tile.lightAsAir).
    // Below the threshold the cell still reads as solid earth, so a barely-dug
    // pit doesn't pop its neighbours bright. Re-bakes the 3×3 on change only.
    void UpdateHoleLighting() {
        Tile tile = World.instance?.GetTileAt(x, y);
        if (tile == null) return;
        int uses = workstation != null ? workstation.uses : 0;
        int depleteAt = Mathf.Max(1, structType.depleteAt);
        bool dugEnough = uses >= depleteAt * 0.1f;
        if (tile.lightAsAir != dugEnough) {
            tile.lightAsAir = dugEnough;
            tile.NotifyBodyDirty();
        }
    }

    // UP-dug pits: once the bowl is mined past 60%, also open whichever L/R faces are
    // clear, so mice can hop in from the sides (below that the dirt's in the way and a
    // diagonal pop-in looks wrong). Side-dug pits enter only via their one chosen door,
    // so they skip this entirely. `uses` only increases, so this wires once and stays.
    void UpdateSideAccess() {
        if (digDir != DigDir.Up || sideDoorsWired || workNode == null) return;
        int uses = workstation != null ? workstation.uses : 0;
        int depleteAt = Mathf.Max(1, structType.depleteAt);
        if (uses < depleteAt * 0.6f) return;

        World w = World.instance;
        bool any = false;
        if (IsOpenStandable(w, x - 1, y, out _)) { WireDoorEdge(workNode, x, y, "left");  any = true; }
        if (IsOpenStandable(w, x + 1, y, out _)) { WireDoorEdge(workNode, x, y, "right"); any = true; }
        sideDoorsWired = true;
        // Reachability set changed — refresh A* connectivity components.
        if (any) w.graph.RebuildComponents();
    }

    void EnsureDishObject() {
        if (dishSr != null) return;
        var dishGo = new GameObject("dish");
        dishGo.transform.SetParent(go.transform, false);
        // Same world position as the platform sprite; rendered in front via
        // sortingOrder bump rather than z offset (URP 2D sorts on order, not z).
        dishGo.transform.localPosition = Vector3.zero;
        dishSr = SpriteMaterialUtil.AddSpriteRenderer(dishGo);
        dishSr.sortingOrder = sr.sortingOrder + 1;
        LightReceiverUtil.SetSortBucket(dishSr);
        // Hide this cell's own tile body so the dish's carved-away region reads
        // as an open hole (background / sky shows through). Solidity is left
        // intact (preservesTile), so neighbours still light against us as solid.
        // The dish's own normal map is bound dynamically in UpdateDishTexture so
        // it follows the receding bowl rather than a static full-tile mask.
        Tile tile = World.instance.GetTileAt(x, y);
        if (tile != null) {
            tile.bodyRenderSuppressed = true;
            tile.NotifyBodyDirty();
        }
        // Inherit any blueprint/structure tint applied to the parent SRs. Listed
        // in tintableSrs so SetTint walks it alongside the main SR.
        if (tintableSrs != null) {
            var extended = new SpriteRenderer[tintableSrs.Length + 1];
            System.Array.Copy(tintableSrs, extended, tintableSrs.Length);
            extended[tintableSrs.Length] = dishSr;
            tintableSrs = extended;
        }
    }

    // 8-bit cardinal+diagonal solidity mask matching the convention used by
    // TileSpriteCache / OverlayGrowthSystem: bit set = that neighbour is solid.
    // 0=L 1=R 2=D 3=U 4=BL 5=BR 6=TL 7=TR.
    static int ComputeNeighborMask8(int tx, int ty) {
        World w = World.instance;
        int m = 0;
        if (IsSolidAt(w, tx - 1, ty    )) m |= 1;
        if (IsSolidAt(w, tx + 1, ty    )) m |= 2;
        if (IsSolidAt(w, tx,     ty - 1)) m |= 4;
        if (IsSolidAt(w, tx,     ty + 1)) m |= 8;
        if (IsSolidAt(w, tx - 1, ty - 1)) m |= 16;
        if (IsSolidAt(w, tx + 1, ty - 1)) m |= 32;
        if (IsSolidAt(w, tx - 1, ty + 1)) m |= 64;
        if (IsSolidAt(w, tx + 1, ty + 1)) m |= 128;
        return m;
    }

    static bool IsSolidAt(World w, int tx, int ty) {
        Tile t = w.GetTileAt(tx, ty);
        return t != null && t.type.solid;
    }

    // Cardinal "buried" mask for the dish substrate sprite (bit layout L=1 R=2
    // D=4 U=8 — set = flat/buried side). Matches the tile soft-edge rule: a side
    // is flat only against a SOLID neighbour of the SAME substrate type; against
    // air or a DIFFERENT type it stays exposed, so the dish carries the same
    // jagged inter-type border the full tile would — e.g. the clay floor
    // overhangs the dirt below with a jagged edge instead of a flat cutoff.
    int ComputeSubstrateCardinalMask() {
        World w = World.instance;
        int m = 0;
        if (SameSubstrate(w, x - 1, y)) m |= 1;
        if (SameSubstrate(w, x + 1, y)) m |= 2;
        if (SameSubstrate(w, x,     y - 1)) m |= 4;
        if (SameSubstrate(w, x,     y + 1)) m |= 8;
        return m;
    }

    bool SameSubstrate(World w, int tx, int ty) {
        Tile t = w.GetTileAt(tx, ty);
        return t != null && t.type.solid && t.type == capturedTile;
    }

    void UpdateDishTexture() {
        // Resample substrate pixels on first call OR when capturedTile changed
        // (saves on load: the field is set after the SR object exists in memory
        // from a prior pit, e.g. across scene reloads in PlayMode tests).
        if (substratePixels == null) {
            Sprite substrate = TileSpriteCache.Get(capturedTile.name, ComputeSubstrateCardinalMask(), x, y);
            if (substrate == null) {
                Debug.LogError($"DiggingPit dish: TileSpriteCache returned null for '{capturedTile.name}' at {x},{y}");
                return;
            }
            substratePixels = substrate.texture.GetPixels32();
        }

        int uses = workstation != null ? workstation.uses : 0;
        if (uses == lastDishUses && dishSprite != null) return;  // nothing changed
        lastDishUses = uses;

        int depleteAt = Mathf.Max(1, structType.depleteAt);
        float progress = Mathf.Clamp01(uses / (float)depleteAt);

        // Remaining-substrate profile in interior-pixel units (0..15). The dish shows
        // the substrate LEFT in the cell: thick at the rim, thinnest (the concave bite)
        // toward the open face. `thickEdge` is the rim thickness, `thickCenter` the
        // bite's deepest thickness; their difference stays DEPTH so the bowl shape is
        // preserved as the whole thing recedes away from the opening.
        //   progress=0 → thickEdge≈14 (barely dented), thickCenter = thickEdge−DEPTH
        //   progress=1 → thickEdge=DEPTH, thickCenter=0 (mined back to the far wall)
        // At progress=1 the pit is destroyed and replaced with a platform, so the
        // visual never actually has to reach a vanishing dish.
        int thickEdge   = Mathf.RoundToInt(Mathf.Lerp(14f, DEPTH, progress));
        int thickCenter = thickEdge - DEPTH;

        if (dishTex == null) {
            dishTex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp
            };
        }

        // Start from the FULL substrate, including its 2px overhang borders — those
        // borders carry the jagged inter-type edges (e.g. the clay/dirt seam), so we
        // keep them and only excavate the bite. The rim-border lanes clamp to t=1
        // (full thickness), so the buried-edge overhang and seam survive while the
        // bite is cleared toward the open face.
        var pixels = (Color32[])substratePixels.Clone();
        var clear  = new Color32(0, 0, 0, 0);
        // Carve toward the open face. `a` runs along the rim (the axis parallel to the
        // opening): columns (px) for an Up bite, rows (py) for a Left/Right bite. The
        // border lanes (a outside the interior) clamp to t=1 → full thickness, so the
        // overhang and the jagged inter-type seam survive on the buried edges.
        for (int a = 0; a < SIZE; a++) {
            float lateralNorm = (a - BORDER + 0.5f) / INNER;       // <0 / >1 in the rim borders
            float t = Mathf.Clamp01(4f * (lateralNorm - 0.5f) * (lateralNorm - 0.5f)); // 0 rim-center, 1 at/over rim edges
            int solid = Mathf.RoundToInt(thickCenter + (thickEdge - thickCenter) * t);
            switch (digDir) {
                case DigDir.Up: {        // opening = top; solid fills from the floor up, clear above it
                    int surfacePy = BORDER + solid;
                    for (int py = surfacePy + 1; py < SIZE; py++) pixels[py * SIZE + a] = clear;
                    break;
                }
                case DigDir.Left: {      // opening = left; solid fills from the right wall, clear to its left
                    int surfacePx = BORDER + INNER - solid;
                    for (int px = 0; px < surfacePx; px++) pixels[a * SIZE + px] = clear;
                    break;
                }
                default: {               // Right: opening = right; solid fills from the left wall, clear to its right
                    int surfacePx = BORDER + solid;
                    for (int px = surfacePx + 1; px < SIZE; px++) pixels[a * SIZE + px] = clear;
                    break;
                }
            }
        }

        dishTex.SetPixels32(pixels);
        dishTex.Apply();

        // Re-bake the normal map from the carved silhouette so the receding bowl
        // is lit as a freshly-cut surface (rim beveled, depths darkened) rather
        // than a flat full tile. mask8 reflects the cell's solid neighbours, so
        // exposed sides (top/left of a surface pit) get edge lighting while
        // buried sides stay dark — consistent with the surrounding earth.
        int mask8 = ComputeNeighborMask8(x, y);
        Texture2D newNormal = TileSpriteCache.BakeMaskedNormalMap(pixels, mask8);
        if (dishNormalTex != null) Object.Destroy(dishNormalTex);
        dishNormalTex = newNormal;
        var mpb = new MaterialPropertyBlock();
        dishSr.GetPropertyBlock(mpb);
        mpb.SetTexture(Shader.PropertyToID("_NormalMap"), dishNormalTex);
        dishSr.SetPropertyBlock(mpb);

        if (dishSprite == null) {
            // PPU=16 matches TileSpriteCache so the dish's 16×16 interior aligns
            // perfectly with the tile cell. Pivot center matches the building's
            // own sprite anchor convention.
            dishSprite = Sprite.Create(dishTex, new Rect(0, 0, SIZE, SIZE),
                new Vector2(0.5f, 0.5f), 16f);
            dishSr.sprite = dishSprite;
        }
    }

    // Track the dish's pixel-snapped floor with the digger's standing position so
    // the worker visually descends with the pit. workNode here is the interior node
    // (repointed by Structure ctor for workstations with interiorTiles) — its wx/wy
    // is purely the visual stand position; the door edges that make it pathable are
    // unaffected by wy changes.
    void UpdateWorkSpot() {
        if (workNode == null) return;
        int uses = workstation != null ? workstation.uses : 0;
        int depleteAt = Mathf.Max(1, structType.depleteAt);
        float progress = Mathf.Clamp01(uses / (float)depleteAt);
        // Bite-peak position in interior-pixel space (0..15), rounded to nearest pixel.
        // `peak` is the deepest point of the concave bite, receding 12 → 0 toward the
        // far wall across the pit's lifetime (matches the dish carve closely enough for
        // the digger to sit on the cut surface).
        int rim  = Mathf.RoundToInt(Mathf.Lerp(14f, 2f, progress));
        int peak = rim - 2;
        if (digDir == DigDir.Up) {
            // Worker descends with the floor. Pixel iy → world y relative to tile centre
            // ((iy − 7)/16); +0.5 because the animal sprite pivot is centred, not at the
            // feet, so the node sits half a tile above the floor for the feet to land there.
            workNode.wx = x;
            workNode.wy = y + (peak - 7) / 16f + 0.5f;
        } else {
            // Side dig: stand on the tunnel floor (row y), offset ~0.4 tile toward the
            // open side from where the bite peaks, tracking the peak as it recedes into
            // the wall. A Left bite cuts in from the right interior edge, a Right bite
            // from the left, so the peak's interior-x differs by direction.
            int peakIx = digDir == DigDir.Left ? INNER - peak : peak;
            float peakWorldX = x + (peakIx - 7) / 16f;
            workNode.wx = digDir == DigDir.Left ? peakWorldX - 0.4f : peakWorldX + 0.4f;
            workNode.wy = y;
        }
    }

    public override void Destroy() {
        // Restore normal cell rendering. The depletion path already swaps the
        // tile to "empty" (so this is a no-op there), but a player deconstruct
        // leaves the original solid tile behind and needs its body back.
        Tile tile = World.instance?.GetTileAt(x, y);
        if (tile != null && (tile.bodyRenderSuppressed || tile.lightAsAir)) {
            tile.bodyRenderSuppressed = false;
            tile.lightAsAir = false;
            tile.NotifyBodyDirty();
        }
        // Tear down the textures we allocated. Sprite/SR are children of `go` and
        // get cleaned up by the base GameObject destroy.
        if (dishTex != null) {
            Object.Destroy(dishTex);
            dishTex = null;
        }
        if (dishNormalTex != null) {
            Object.Destroy(dishNormalTex);
            dishNormalTex = null;
        }
        dishSprite = null;
        dishSr = null;
        substratePixels = null;
        base.Destroy();
    }
}
