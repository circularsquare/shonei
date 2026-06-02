using UnityEngine;

// DiggingPit produces the substrate it was built on (dirt, sand, or clay).
// The original tile type is captured at placement time — before StructController
// replaces the tile with empty — and resolved to the substrate's primary product
// each craft cycle via GetExtractionOutputs.
//
// The digging pit recipe in recipesDb.json deliberately has empty noutputs; the
// craft hook in AnimalStateManager routes pit output through this class instead.
// Persisted across save/load by name (see SaveSystem.GatherStructure /
// RestoreStructure), reusing the same `capturedTileType` field as Quarry.
//
// ── Dish visual ─────────────────────────────────────────────────────────
// The pit renders in two layers: the platform sprite (yellow frame, from the
// standard StructureVisualBuilder path) plus a dynamically generated "earth
// dish" sprite rendered in front. The dish is a 20×20 texture built from the
// captured tile's interior pixels (cardinal mask 15) with a parabolic mask
// carved out — high at the edges, lowest in the center — so it shows the
// REMAINING substrate as a partially excavated bowl. As uses accumulates, both
// the edges and the center drop, and the workspot drops with them so the digger
// appears to stand on the receding floor. On depletion the pit is replaced by a
// regular platform (see AnimalStateManager.HandleWorking).
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
public class DiggingPit : Building {
    public TileType capturedTile;

    // Dish layer: separate SR rendered in front of the platform. Owned and
    // torn down by this class so the texture allocation doesn't leak.
    SpriteRenderer dishSr;
    Texture2D dishTex;
    Texture2D dishNormalTex; // carve-matched normal map; re-baked when uses changes, owned here
    Sprite dishSprite;
    Color32[] substratePixels;
    int lastDishUses = -1; // skip texture rebuilds when uses hasn't changed

    // Side-door gating. While the dish is > 40% full (progress < 0.6), only the
    // top door is reachable — mice popping in diagonally from the side looks weird
    // when there's still a lot of dirt in the way. Once enough has been mined out,
    // the L/R door edges get added back. References captured the first time we
    // see them on the interior node's neighbour list; null = that side was never
    // wired (e.g. off-map, or door declaration missing).
    Node leftApproach;
    Node rightApproach;
    bool sidesCaptured;     // have we scanned for L/R neighbours yet?
    bool sidesConnected = true;  // current edge state; true matches Structure ctor's default

    public DiggingPit(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    // Called by StructController.Construct before the underlying tile is emptied.
    // Invalid inputs log an error and leave capturedTile null so the fallback
    // path (recipe.outputs) kicks in — guarantees we never silently produce nothing.
    public void CaptureOriginalTile(TileType t) {
        if (t == null || !t.solid) {
            Debug.LogError($"DiggingPit.CaptureOriginalTile: invalid tile at {x},{y} (null or non-solid)");
            return;
        }
        capturedTile = t;
    }

    // Returns the substrate's primary product at 1 liang per craft (matches the original
    // `dirt × 1` recipe quantity), plus a 10% bonus clay nodule when digging dirt or sand —
    // alluvial pockets in real soils occasionally turn up clay even outside formal clay
    // beds. Clay substrate skips the bonus since it's already producing clay as primary.
    // Null on bad state → AnimalStateManager falls back to the recipe's (empty) outputs.
    public ItemQuantity[] GetExtractionOutputs() {
        if (capturedTile == null) {
            Debug.LogError($"DiggingPit at {x},{y} has no capturedTile — falling back to recipe outputs");
            return null;
        }
        if (capturedTile.products == null || capturedTile.products.Length == 0) {
            Debug.LogError($"DiggingPit at {x},{y}: tile '{capturedTile.name}' has no nproducts defined");
            return null;
        }
        var primary = new ItemQuantity(capturedTile.products[0].item, ItemStack.LiangToFen(1f));
        if (capturedTile.name == "dirt" || capturedTile.name == "sand") {
            var bonus = new ItemQuantity(Db.itemByName["clay"], ItemStack.LiangToFen(1f));
            bonus.chance = 0.10f;
            return new[] { primary, bonus };
        }
        return new[] { primary };
    }

    public override void OnPlaced() {
        base.OnPlaced();
        RebuildDishVisual();
    }

    // Called from AnimalStateManager after each completed craft round, and from
    // SaveSystem.RestoreStructure once capturedTile has been restored.
    public void RebuildDishVisual() {
        if (capturedTile == null) return;       // nothing to draw yet (pre-capture or load mid-restore)
        if (go == null) return;                  // structure has been torn down
        EnsureDishObject();
        UpdateDishTexture();
        UpdateWorkSpot();
        UpdateSideAccess();
        UpdateHoleLighting();
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

    // Add or remove the L/R door edges depending on dish progress. The top edge
    // stays wired forever; sides only join when the dish has been mined down enough
    // that a side approach reads as "into the open" rather than "diagonally through
    // dirt". Threshold matches the user's 40%-full callout (progress < 0.6 = sides off).
    void UpdateSideAccess() {
        if (workNode == null) return;
        // Capture references on first call (after ctor has already wired everything).
        if (!sidesCaptured) {
            foreach (Node n in workNode.neighbors) {
                if (n.tile == null) continue;
                if (n.x == x - 1 && n.y == y) leftApproach  = n;
                else if (n.x == x + 1 && n.y == y) rightApproach = n;
            }
            sidesCaptured = true;
        }
        if (leftApproach == null && rightApproach == null) return;

        int uses = workstation != null ? workstation.uses : 0;
        int depleteAt = Mathf.Max(1, structType.depleteAt);
        float progress = Mathf.Clamp01(uses / (float)depleteAt);
        bool shouldConnect = progress >= 0.6f;
        if (shouldConnect == sidesConnected) return;

        if (shouldConnect) {
            if (leftApproach  != null) workNode.AddNeighbor(leftApproach,  reciprocal: true);
            if (rightApproach != null) workNode.AddNeighbor(rightApproach, reciprocal: true);
        } else {
            if (leftApproach  != null) { workNode.RemoveNeighbor(leftApproach);  leftApproach.RemoveNeighbor(workNode);  }
            if (rightApproach != null) { workNode.RemoveNeighbor(rightApproach); rightApproach.RemoveNeighbor(workNode); }
        }
        sidesConnected = shouldConnect;
        // Reachability set may have changed — refresh A* connectivity components.
        World.instance.graph.RebuildComponents();
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

        const int SIZE   = 20;  // matches TileSpriteCache.SIZE
        const int BORDER = 2;   // 2px overhang on each side of the 20px sprite
        const int INNER  = 16;  // 16×16 interior
        const int DEPTH  = 3;   // constant edge-minus-center, in pixels

        int depleteAt = Mathf.Max(1, structType.depleteAt);
        float progress = Mathf.Clamp01(uses / (float)depleteAt);

        // Parabolic dish profile in interior-pixel space (0..15 from interior bottom).
        // The whole bowl translates downward uniformly as progress increases; depth
        // (edge minus center) stays fixed at DEPTH so the shape is preserved as the
        // pit empties. Endpoints:
        //   progress=0 → edge at iy=14 (1px below tile top) → bottom at iy=12
        //   progress=1 → bottom at floor (iy=0)              → edge at iy=2
        // At progress=1 the pit is destroyed and replaced with a platform, so the
        // visual never actually has to reach a vanishing dish.
        int edgeTopPx      = Mathf.RoundToInt(Mathf.Lerp(14f, DEPTH, progress));
        int centerBottomPx = edgeTopPx - DEPTH;

        if (dishTex == null) {
            dishTex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false) {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp
            };
        }

        // Start from the FULL substrate, including its 2px overhang borders —
        // those borders carry the jagged inter-type edges (e.g. the clay/dirt
        // seam lives in the bottom border rows, not the flat interior). Then
        // excavate everything strictly ABOVE the parabolic bowl surface.
        // Building interior-only (the previous approach) clipped the bottom to a
        // flat interior row and lost the jagged seam. Side border columns use a
        // clamped profile (t=1 at and beyond the edges), so the left cliff edge
        // and the bottom seam survive while the dug-out top is cleared.
        var pixels = (Color32[])substratePixels.Clone();
        var clear  = new Color32(0, 0, 0, 0);
        for (int px = 0; px < SIZE; px++) {
            float xNorm = (px - BORDER + 0.5f) / INNER;            // <0 / >1 in the side borders
            float t = Mathf.Clamp01(4f * (xNorm - 0.5f) * (xNorm - 0.5f)); // 0 center, 1 at/over edges
            int surfaceIy = Mathf.RoundToInt(centerBottomPx + (edgeTopPx - centerBottomPx) * t);
            int surfacePy = BORDER + surfaceIy;                    // clear strictly above the surface
            for (int py = surfacePy + 1; py < SIZE; py++) pixels[py * SIZE + px] = clear;
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
        // Match the dish-texture math: bowl floor in interior-pixel-space rounded
        // to nearest pixel. centerBottomPx ranges 12 → 0 across the lifetime.
        int edgeTopPx      = Mathf.RoundToInt(Mathf.Lerp(14f, 2f, progress));
        int centerBottomPx = edgeTopPx - 2;
        // Convert interior pixel index (0..15) to world y relative to tile center.
        // Pixel iy spans world y in [(iy − 8)/16, (iy − 7)/16]; the worker's feet
        // sit on the top edge of the floor pixel, so the upper edge of pixel
        // centerBottomPx — i.e. (centerBottomPx − 7) / 16 — is the stand height.
        // Plus 0.5: animal sprite pivot is centred, not at the feet, so workNode.wy
        // needs to be half a tile higher than the floor for the feet to land there.
        workNode.wy = y + (centerBottomPx - 7) / 16f + 0.5f;
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
