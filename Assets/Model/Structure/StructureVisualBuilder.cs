using UnityEngine;

// ── StructureVisualBuilder ──────────────────────────────────────────────
//
// Single source of truth for spawning a structure's "primary visual" — the
// sprite renderers that depict the building itself. Used by three callers:
//
//   - Structure ctor              (built form, full opacity)
//   - Blueprint ctor              (translucent ghost during construction)
//   - MouseController build preview (cursor-following ghost in build mode)
//
// Pre-refactor each site re-implemented the standard {anchor sprite + optional
// _b/_m/_t vertical extensions} logic, with subtle drift over time. This
// helper consolidates that into one path, AND adds a per-name dispatch hook
// for "custom visual" structures (currently: tarp; future: tassels, banners,
// etc.) so adding a new custom visual is a single new branch in `Build` —
// not three parallel edits.
//
// ── Refs ────────────────────────────────────────────────────────────────
// Build returns a `Refs` struct so callers can store what they need:
//
//   - `mainSr`        — the anchor SpriteRenderer. For custom-visual types
//                       this is created but `enabled = false`; we keep it so
//                       downstream code reading `sr.sortingOrder` (PortStubs,
//                       MaintenanceSystem material swaps, deconstruct overlay)
//                       still works without per-subclass null-checks.
//   - `extensionSrs`  — _b/_m/_t children for shape-aware vertical structures.
//                       Empty for custom-visual or 1×1 types.
//   - `customSrs`     — extras spawned by custom-visual branches (tarp's
//                       cloth + posts, future tassel chain pieces, etc.).
//                       Empty for the standard path.
//   - `tintableSrs`   — flattened union of the above. `Structure.SetTint` /
//                       `Blueprint.SetTint` walk this so the deconstruct red
//                       overlay reaches every spawned SR — including custom
//                       children — without per-subclass code.
//
public static class StructureVisualBuilder {
    public struct Refs {
        public SpriteRenderer    mainSr;
        public bool              mainSpriteWasFallback;
        public SpriteRenderer[]  extensionSrs;
        public SpriteRenderer[]  customSrs;
        public SpriteRenderer[]  tintableSrs;
    }

    // Builds the primary visual for `st` on `parent`. Caller passes the final
    // sortingOrder it wants the main SR at (depth-derived for built structures,
    // 100 for blueprints, 200 for build previews) and a multiplicative tint
    // (Color.white for built; (0.8,0.9,1,0.5) for blueprint; (1,1,1,0.3) for
    // preview). Rotation is applied to `parent.transform` here so all three
    // callers stay symmetric.
    public static Refs Build(GameObject parent, StructType st, Shape shape,
                             bool mirrored, int rotation,
                             int baseSortingOrder, Color tint) {
        parent.transform.rotation = StructureVisuals.RotationFor(rotation);
        if (st.name == "tarp") return BuildTarp(parent, shape, baseSortingOrder, tint);
        return BuildStandard(parent, st, shape, mirrored, baseSortingOrder, tint);
    }

    // Standard path: anchor sprite + optional _b/_m/_t vertical extensions.
    // Mirrors what Structure / Blueprint / MouseController each used to do
    // independently.
    static Refs BuildStandard(GameObject parent, StructType st, Shape shape,
                              bool mirrored, int baseSortingOrder, Color tint) {
        Sprite sprite = StructureVisuals.ResolveAnchorSprite(st, shape, out bool wasFallback);
        SpriteRenderer mainSr = SpriteMaterialUtil.AddSpriteRenderer(parent);
        mainSr.sprite       = sprite;
        mainSr.flipX        = mirrored;
        mainSr.color        = tint;
        mainSr.sortingOrder = baseSortingOrder;
        if (wasFallback) {
            // Fallback sprite is a 1×1 default — slice it to fill the footprint
            // so the player still sees the placement extent.
            mainSr.drawMode = SpriteDrawMode.Sliced;
            mainSr.size     = new Vector2(st.nx, Mathf.Max(1, st.ny));
        }
        LightReceiverUtil.SetSortBucket(mainSr);

        SpriteRenderer[] extensions;
        bool shapeAware = st.HasShapes;
        if (shapeAware && shape.nx == 1 && shape.ny > 1) {
            int extCount = shape.ny - 1;
            extensions = new SpriteRenderer[extCount];
            for (int i = 0; i < extCount; i++) {
                int dy = i + 1;
                GameObject extGo = new GameObject($"ext{dy}");
                extGo.transform.SetParent(parent.transform, false);
                extGo.transform.localPosition = new Vector3(0f, dy, 0f);
                SpriteRenderer extSr = SpriteMaterialUtil.AddSpriteRenderer(extGo);
                extSr.sprite       = StructureVisuals.LoadShapeSprite(st, shape, dy);
                extSr.sortingOrder = baseSortingOrder;
                extSr.flipX        = mirrored;
                extSr.color        = tint;
                LightReceiverUtil.SetSortBucket(extSr);
                extensions[i] = extSr;
            }
        } else {
            extensions = System.Array.Empty<SpriteRenderer>();
        }

        return new Refs {
            mainSr                = mainSr,
            mainSpriteWasFallback = wasFallback,
            extensionSrs          = extensions,
            customSrs             = System.Array.Empty<SpriteRenderer>(),
            tintableSrs           = BuildTintable(mainSr, extensions, null),
        };
    }

    // Custom-visual path for tarps. Spawns a disabled main SR (kept for
    // sortingOrder/SetTint compatibility) plus cloth + 2 posts via TarpVisuals.
    static Refs BuildTarp(GameObject parent, Shape shape, int baseSortingOrder, Color tint) {
        SpriteRenderer mainSr = SpriteMaterialUtil.AddSpriteRenderer(parent);
        mainSr.enabled      = false;
        mainSr.sortingOrder = baseSortingOrder;
        mainSr.color        = tint;
        LightReceiverUtil.SetSortBucket(mainSr);

        TarpVisuals.Refs tarpRefs = TarpVisuals.Spawn(parent, shape.ny);
        TarpVisuals.Layout(tarpRefs, shape.nx, shape.ny);
        TarpVisuals.Tint(tarpRefs, tint, baseSortingOrder);

        SpriteRenderer[] customs = TarpVisuals.AllSrs(tarpRefs);
        return new Refs {
            mainSr                = mainSr,
            mainSpriteWasFallback = false,
            extensionSrs          = System.Array.Empty<SpriteRenderer>(),
            customSrs             = customs,
            tintableSrs           = BuildTintable(mainSr, null, customs),
        };
    }

    static SpriteRenderer[] BuildTintable(SpriteRenderer mainSr,
                                          SpriteRenderer[] extensions,
                                          SpriteRenderer[] customs) {
        int n = (mainSr != null ? 1 : 0)
              + (extensions != null ? extensions.Length : 0)
              + (customs    != null ? customs.Length    : 0);
        var arr = new SpriteRenderer[n];
        int i = 0;
        if (mainSr != null) arr[i++] = mainSr;
        if (extensions != null) for (int j = 0; j < extensions.Length; j++) arr[i++] = extensions[j];
        if (customs    != null) for (int j = 0; j < customs.Length;    j++) arr[i++] = customs[j];
        return arr;
    }

}
