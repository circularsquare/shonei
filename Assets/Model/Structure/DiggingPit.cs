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
// carved out — high at the edges, lowest in the center — so it looks like a
// partially excavated bowl of the original substrate. As uses accumulates,
// both the edges and the center drop, and the workspot drops with them so the
// digger appears to stand on the receding floor of the pit. On depletion the
// pit is replaced by a regular platform (see AnimalStateManager.HandleWorking).
public class DiggingPit : Building {
    public TileType capturedTile;

    // Dish layer: separate SR rendered in front of the platform. Owned and
    // torn down by this class so the texture allocation doesn't leak.
    SpriteRenderer dishSr;
    Texture2D dishTex;
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
        // Bind the substrate's full-tile normal map so the dish gets the same
        // edge-distance darkening a regular tile would at this position — bright
        // near exposed sides, darker deep in the interior. Without this MPB
        // binding the sprite's _NormalMap defaults to flat (alpha=1 everywhere),
        // so the dish reads as uniformly lit and inconsistent with neighbouring
        // dirt. The mask reflects the tile's actual neighbours, so a surface pit
        // gets a top-bright/bottom-dark gradient while a buried pit reads
        // uniformly dark — same as the dirt block it carves into.
        int mask8 = ComputeNeighborMask8(x, y);
        Texture2D normalMap = TileSpriteCache.GetNormalMap(capturedTile.name, mask8, x, y);
        if (normalMap != null) {
            var mpb = new MaterialPropertyBlock();
            dishSr.GetPropertyBlock(mpb);
            mpb.SetTexture(Shader.PropertyToID("_NormalMap"), normalMap);
            dishSr.SetPropertyBlock(mpb);
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

    void UpdateDishTexture() {
        // Resample substrate pixels on first call OR when capturedTile changed
        // (saves on load: the field is set after the SR object exists in memory
        // from a prior pit, e.g. across scene reloads in PlayMode tests).
        if (substratePixels == null) {
            Sprite substrate = TileSpriteCache.Get(capturedTile.name, 15, x, y);
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

        var pixels = new Color32[SIZE * SIZE];  // default: clear (0,0,0,0)
        for (int py = BORDER; py < BORDER + INNER; py++) {
            int iy = py - BORDER;
            for (int px = BORDER; px < BORDER + INNER; px++) {
                int ix = px - BORDER;
                float xNorm = (ix + 0.5f) / INNER;            // pixel center across the full interior
                float t = 4f * (xNorm - 0.5f) * (xNorm - 0.5f); // 0 at center, 1 at edges
                int surfacePx = Mathf.RoundToInt(centerBottomPx + (edgeTopPx - centerBottomPx) * t);
                if (iy <= surfacePx) {
                    pixels[py * SIZE + px] = substratePixels[py * SIZE + px];
                }
            }
        }

        dishTex.SetPixels32(pixels);
        dishTex.Apply();

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
        // Tear down the texture we allocated. Sprite/SR are children of `go` and
        // get cleaned up by the base GameObject destroy.
        if (dishTex != null) {
            Object.Destroy(dishTex);
            dishTex = null;
        }
        dishSprite = null;
        dishSr = null;
        substratePixels = null;
        base.Destroy();
    }
}
