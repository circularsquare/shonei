using UnityEngine;

// ── Tarp ────────────────────────────────────────────────────────────────
//
// Decorative cloth strung between two posts. Footprint is a horizontal row of
// shape.nx tiles on the platform layer (depth 1).
//
// Visual: spawned by StructureVisualBuilder via the per-name dispatch in
// `Build` (see StructureVisualBuilder.cs). The same spawn path is shared
// across the built structure, the blueprint ghost, and the build-mode
// placement preview; the three differ only in tint and sortingOrder. The
// actual cloth + posts layout lives in the TarpVisuals helper below.
//
// Stretching: the cloth uses `SpriteDrawMode.Sliced`. Configure 9-slice
// borders on tarp_cloth.png in the Sprite Editor to control which pixels
// stay constant vs. stretch. For a flat horizontal cloth with no border art,
// leave borders at 0 — the whole sprite stretches uniformly.
//
// Gameplay role: blocks rain (StructType.blocksRain=true). Tiles directly
// below the tarp report `IsExposedAbove == false`, which gates rain-catch
// tanks, soil moisture uptake, and any future rain-aware system. solidTop is
// intentionally false — tarps aren't walkable, items don't rest on them, and
// they don't cast hard shadow boundaries.
//
// The Tarp class itself is intentionally near-empty — kept as a Building
// subclass so Structure.Create has a hook to dispatch to, and so future
// tarp-specific gameplay behaviour (per-tarp colour, sway, etc.) has a home.
public class Tarp : Building {
    public Tarp(StructType st, int x, int y, bool mirrored = false, int shapeIndex = 0)
        : base(st, x, y, mirrored, shapeIndex) { }
}

// ── TarpVisuals helper ──────────────────────────────────────────────────
//
// Shared layout/tint logic for tarp visuals. Used by:
//   - Tarp.AttachAnimations (built structure)
//   - Blueprint ctor (translucent ghost during construction)
//   - MouseController build preview (cursor-following ghost in build mode)
//
// All three callers want the same 3-SR arrangement (cloth + 2 posts, right
// post flipX-mirrored) at different tints/sortingOrders. The static helpers
// here let each caller spawn once, then re-Layout / re-Tint cheaply.
public static class TarpVisuals {
    public struct Refs {
        public SpriteRenderer    cloth;
        public SpriteRenderer[]  leftPosts;   // bottom-up; index 0 = dy=0 (_b slice)
        public SpriteRenderer[]  rightPosts;  // same; flipX = true
    }

    // Spawns the cloth + per-tile post SRs for an ny-tall tarp on `parent`.
    //
    // Posts use the standard `_b/_m/_t` vertical-slice convention from the platform
    // pattern (see SPEC-rendering §Normal maps "Slice awareness" / "Slice Vertical
    // Building Sheet"): tarp_post.png is a 16×N sheet sliced bottom→top into
    // tarp_post_b / tarp_post_m / tarp_post_t. For each post column we spawn ny
    // SRs and assign them by dy (0 → _b, ny-1 → _t, otherwise _m). For ny=1 the
    // single SR uses the _b slice — matching the user's "just use the bottom
    // sprite" rule for short tarps.
    //
    // Falls back gracefully when the sheet isn't sliced yet — uses the first sprite
    // in the sheet for every dy (visually duplicates art, but doesn't crash).
    //
    // Missing-asset cases log a warning once. Caller MUST follow up with Layout(...)
    // and Tint(...) — Spawn does not set positions, colors, or sortingOrders.
    public static Refs Spawn(GameObject parent, int ny) {
        var refs = new Refs();
        ny = Mathf.Max(1, ny);

        // Cloth — single sprite, drawMode=Sliced. Stretched horizontally in Layout.
        Sprite clothSprite = Resources.Load<Sprite>("Sprites/Buildings/tarp_cloth");
        if (clothSprite != null) {
            GameObject g = new GameObject("tarp_cloth");
            g.transform.SetParent(parent.transform, false);
            SpriteRenderer s = SpriteMaterialUtil.AddSpriteRenderer(g);
            s.sprite   = clothSprite;
            s.drawMode = SpriteDrawMode.Sliced;
            refs.cloth = s;
        } else {
            Debug.LogWarning("TarpVisuals: Sprites/Buildings/tarp_cloth.png not found — cloth won't render. " +
                             "Author a horizontally-stretchable cloth sprite.");
        }

        // Posts — per-tile SRs loaded from the tarp_post sheet's slices.
        // Prefers `tarp_post_s.png` (the sliced sheet — required when a 1×1
        // `tarp_post.png` already exists) and falls back to `tarp_post.png` for
        // the single-sprite case. Slice names stay `tarp_post_b/_m/_t` regardless
        // of which file they come from. See SPEC-rendering §Normal maps "Slice
        // awareness" for the convention.
        Sprite[] postSheet = Resources.LoadAll<Sprite>("Sprites/Buildings/tarp_post_s");
        if (postSheet == null || postSheet.Length == 0)
            postSheet = Resources.LoadAll<Sprite>("Sprites/Buildings/tarp_post");
        if (postSheet != null && postSheet.Length > 0) {
            refs.leftPosts  = new SpriteRenderer[ny];
            refs.rightPosts = new SpriteRenderer[ny];
            for (int dy = 0; dy < ny; dy++) {
                Sprite slice = ResolvePostSlice(postSheet, dy, ny);
                refs.leftPosts[dy]  = SpawnPost(parent, $"tarp_post_left_{dy}",  slice, flipX: false);
                refs.rightPosts[dy] = SpawnPost(parent, $"tarp_post_right_{dy}", slice, flipX: true);
            }
        } else {
            Debug.LogWarning("TarpVisuals: Sprites/Buildings/tarp_post.png not found — posts won't render. " +
                             "Author a vertical-pole sprite (16×N sliced into _b/_m/_t for tall tarps).");
        }

        return refs;
    }

    // Picks the right slice from the tarp_post sheet for a given dy in an ny-tall
    // post column. Convention: dy=0 → _b, dy=ny-1 → _t, otherwise _m. ny=1 always
    // uses _b. Falls back to the first sprite in the sheet if the named slice isn't
    // found (handles the un-sliced single-sprite case).
    static Sprite ResolvePostSlice(Sprite[] sheet, int dy, int ny) {
        string suffix = ny == 1 ? "_b"
                       : dy == 0 ? "_b"
                       : dy == ny - 1 ? "_t"
                       : "_m";
        string name = "tarp_post" + suffix;
        for (int i = 0; i < sheet.Length; i++)
            if (sheet[i] != null && sheet[i].name == name) return sheet[i];
        return sheet[0];
    }

    static SpriteRenderer SpawnPost(GameObject parent, string name, Sprite sprite, bool flipX) {
        GameObject g = new GameObject(name);
        g.transform.SetParent(parent.transform, false);
        SpriteRenderer s = SpriteMaterialUtil.AddSpriteRenderer(g);
        s.sprite = sprite;
        s.flipX  = flipX;
        return s;
    }

    // Positions cloth + posts for the given footprint width × height. Cheap — just
    // transform / size writes; safe to call every frame.
    //
    // Layout:
    //   - Posts at the leftmost (localX=0) and rightmost (localX=nx-1) columns, one
    //     SR per tile in the column. localY matches the tile's dy (0..ny-1).
    //   - Cloth sits at the TOP of the structure (localY=ny-1), strung between the
    //     two post tops, sliced to span (nx-1) units horizontally and 1 unit tall.
    public static void Layout(Refs refs, int nx, int ny) {
        nx = Mathf.Max(2, nx);
        ny = Mathf.Max(1, ny);
        if (refs.cloth != null) {
            refs.cloth.transform.localPosition = new Vector3((nx - 1) * 0.5f, ny - 1, 0f);
            refs.cloth.size = new Vector2(nx - 1, 1f);
        }
        LayoutColumn(refs.leftPosts,  localX: 0f);
        LayoutColumn(refs.rightPosts, localX: nx - 1f);
    }

    static void LayoutColumn(SpriteRenderer[] column, float localX) {
        if (column == null) return;
        for (int dy = 0; dy < column.Length; dy++) {
            if (column[dy] != null)
                column[dy].transform.localPosition = new Vector3(localX, dy, 0f);
        }
    }

    // Applies tint and sortingOrder. Cloth sorts at baseSortingOrder + 1 so it
    // draws above the posts (visually drapes over their tops); posts sort at
    // baseSortingOrder. Caller passes whatever base order is appropriate for
    // their context (built structure: structure's sortingOrder; blueprint: 100;
    // build preview: 200).
    public static void Tint(Refs refs, Color color, int baseSortingOrder) {
        if (refs.cloth != null) {
            refs.cloth.color = color;
            refs.cloth.sortingOrder = baseSortingOrder + 1;
            LightReceiverUtil.SetSortBucket(refs.cloth);
        }
        TintColumn(refs.leftPosts,  color, baseSortingOrder);
        TintColumn(refs.rightPosts, color, baseSortingOrder);
    }

    static void TintColumn(SpriteRenderer[] column, Color color, int sortingOrder) {
        if (column == null) return;
        for (int i = 0; i < column.Length; i++) {
            if (column[i] != null) {
                column[i].color = color;
                column[i].sortingOrder = sortingOrder;
                LightReceiverUtil.SetSortBucket(column[i]);
            }
        }
    }

    // Bulk SetActive — used by the build preview to hide the layout when the
    // player switches to a non-tarp build (avoids destroying/respawning).
    public static void SetActive(Refs refs, bool active) {
        if (refs.cloth != null) refs.cloth.gameObject.SetActive(active);
        SetActiveColumn(refs.leftPosts,  active);
        SetActiveColumn(refs.rightPosts, active);
    }

    static void SetActiveColumn(SpriteRenderer[] column, bool active) {
        if (column == null) return;
        for (int i = 0; i < column.Length; i++)
            if (column[i] != null) column[i].gameObject.SetActive(active);
    }

    // Returns every spawned SR (cloth + all post SRs, non-null only). Used by
    // StructureVisualBuilder to populate the structure's tintableSrs list — keeps
    // tarp's custom children in the SetTint walk for deconstruct overlays etc.
    public static SpriteRenderer[] AllSrs(Refs refs) {
        int n = (refs.cloth != null ? 1 : 0)
              + CountNonNull(refs.leftPosts)
              + CountNonNull(refs.rightPosts);
        var arr = new SpriteRenderer[n];
        int i = 0;
        if (refs.cloth != null) arr[i++] = refs.cloth;
        i = AppendNonNull(refs.leftPosts,  arr, i);
        i = AppendNonNull(refs.rightPosts, arr, i);
        return arr;
    }

    static int CountNonNull(SpriteRenderer[] a) {
        if (a == null) return 0;
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != null) n++;
        return n;
    }

    static int AppendNonNull(SpriteRenderer[] src, SpriteRenderer[] dst, int dstIdx) {
        if (src == null) return dstIdx;
        for (int i = 0; i < src.Length; i++)
            if (src[i] != null) dst[dstIdx++] = src[i];
        return dstIdx;
    }
}
