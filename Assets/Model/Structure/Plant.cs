using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Runtime.Serialization;

// Plant structure — grows through stages over time, becomes harvestable, and yields
// items via WOM Harvest orders. PlantType (loaded from plantsDb.json) defines growth
// timing, products, and eligibility rules. Registered as a Structure so it lives on
// the tile grid, but lifecycle (grow/harvest/destroy) is plant-specific.
public class Plant : Structure {
    public PlantType plantType;

    public float timer;
    public int age;
    public int growthStage;
    public int size;
    public int yield;

    public bool harvestable;

    // Player-set flag: does this plant get harvested when ripe? Gates the WOM harvest order.
    // Persistent across regrowth cycles — Harvest() leaves it set, so flagged plants keep
    // being harvested each time they mature until the player explicitly unflags.
    // Mutated only via SetHarvestFlagged so the WOM order and overlay stay in sync.
    public bool harvestFlagged { get; private set; }

    // Current tile-height of this plant (1..plantType.maxHeight). Derived from
    // growthStage: `1 + growthStage/4`. Increments cost an above-tile claim on top
    // of the usual per-stage moisture cost — see Grow.
    public int height { get; private set; } = 1;

    // Child GameObjects for tiles above the anchor. extensionSrs[i] renders the tile
    // at (tile.x, tile.y + i + 1). Created on extension, destroyed on harvest/destroy.
    private readonly List<GameObject>     extensionGos = new List<GameObject>();
    private readonly List<SpriteRenderer> extensionSrs = new List<SpriteRenderer>();

    // Per-instance wind-sway phase offset — derived from world coords once at
    // ctor and reused for every SR (anchor + extensions) so all tiles of one
    // plant oscillate in lockstep, but different plants are out of phase with
    // each other. Not persisted; re-derived on load.
    private float plantPhase;

    // True when this plant has a baked blob-sway set (PlantBlobBaker output).
    // When enabled, each tile's main SR shows the static-layer sprite and we
    // spawn one child SR per blob; PlantController.Update walks the per-blob
    // list each frame and translates them by sin(t + φ) * amplitude * wind,
    // pixel-snapped. Plants without a baked set stay on the existing shader-
    // sway path entirely.
    private bool hasBlobSway;

    // Per-blob runtime state for swaying plants. One entry per child blob SR
    // across every tile (anchor + extensions). Flat because PlantController's
    // hot loop wants minimal indirection — tile membership is implicit in
    // each blob's parent transform.
    private class BlobRuntime {
        public Transform      tx;
        public SpriteRenderer sr;
        public float          phase;
        public bool           isStatic;
    }
    private readonly List<BlobRuntime> blobs       = new List<BlobRuntime>();
    private readonly List<GameObject>  blobGos     = new List<GameObject>();

    // ── sway tunables ────────────────────────────────────────────────────────
    // Max amplitude in pixels — peak displacement at |wind| = 1. Displacement
    // is continuous (no integer rounding), so motion can sit at any fractional
    // pixel value; point-filtered sprites resolve sub-pixel transforms into
    // pixel-snapped rendering automatically.
    private const float SwayAmplitudePx = 1f;
    // Radians per second — one cycle every ~4 seconds. Matches the feel of
    // the previous baked-frame loop.
    private const float SwaySpeed       = Mathf.PI / 2f;
    private const float PixelSize       = 1f / 16f;       // matches Plant PPU

    private GameObject     overlayGo;
    private SpriteRenderer overlaySr;

    // Shared unlit material for overlays. SpriteRenderer.AddComponent defaults to lit —
    // which renders black when the GameObject is on the Unlit layer (no light input).
    private static Material _unlitOverlayMaterial;
    private static Material GetUnlitOverlayMaterial() {
        if (_unlitOverlayMaterial != null) return _unlitOverlayMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (shader == null) {
            Debug.LogError("Plant: URP Sprite-Unlit-Default shader not found — harvest overlay will render black");
            return null;
        }
        _unlitOverlayMaterial = new Material(shader) { name = "HarvestOverlayUnlit" };
        return _unlitOverlayMaterial;
    }

    // Cached sprite + layer — avoid per-Plant-ctor Resources.Load and LayerMask.NameToLayer.
    // Sentinel bools so "missing" logs once even though the cached value stays null.
    private static Sprite _harvestOverlaySprite;
    private static bool   _harvestOverlaySpriteLoaded;
    private static Sprite GetHarvestOverlaySprite() {
        if (_harvestOverlaySpriteLoaded) return _harvestOverlaySprite;
        _harvestOverlaySprite = Resources.Load<Sprite>("Sprites/Misc/harvestselect");
        if (_harvestOverlaySprite == null)
            Debug.LogError("Plant: missing Resources/Sprites/Misc/harvestselect — harvest flag overlay will be invisible");
        _harvestOverlaySpriteLoaded = true;
        return _harvestOverlaySprite;
    }
    private static int  _unlitLayer        = -1;
    private static bool _unlitLayerLookedUp;
    private static int GetUnlitLayer() {
        if (_unlitLayerLookedUp) return _unlitLayer;
        _unlitLayer = LayerMask.NameToLayer("Unlit");
        if (_unlitLayer < 0) Debug.LogError("Plant: 'Unlit' layer not found — harvest overlay will be lit");
        _unlitLayerLookedUp = true;
        return _unlitLayer;
    }


    public Plant(PlantType plantType, int x, int y) : base (plantType, x, y){ // call parent constructor
        this.plantType = plantType;

        PlantController.instance.AddPlant(this);
        go.transform.SetParent(PlantController.instance.transform, true);
        go.name = "plant_" + plantType.name;

        sprite = plantType.LoadSprite() ?? Resources.Load<Sprite>("Sprites/Plants/default");
        sr.sprite = sprite;
        sr.sortingOrder = 60;
        LightReceiverUtil.SetSortBucket(sr);

        // Anchor SR was spawned by StructureVisualBuilder with the standard lit
        // material; swap to the plant-sway variant so wind-vertex displacement
        // applies. Done here rather than via a flag in the shared builder
        // because plants are the only structure type that needs this.
        var plantMat = SpriteMaterialUtil.PlantSpriteMaterial;
        if (plantMat != null) sr.sharedMaterial = plantMat;

        plantPhase = ComputePlantPhase(x, y);
        RefreshSwayMPB();

        CreateHarvestOverlay();

        TryEnableBlobSway();
    }

    // If the plant ships a baked blob-sway set (sway_meta + per-blob sprites
    // from PlantBlobBaker), flip the plant onto the blob-sway path: swap the
    // anchor SR to the non-sway lit material, register with PlantController
    // for the per-frame Update loop, and run UpdateSprite once so the static
    // layer + child blob SRs are wired up immediately. Plants without baked
    // sway stay on the existing shader-sway path entirely.
    private void TryEnableBlobSway() {
        string n = plantType.name.Replace(" ", "");
        if (PlantSwayMetaCache.Get(n) == null) return;
        hasBlobSway = true;

        var lit = SpriteMaterialUtil.LitSpriteMaterial;
        if (lit != null) sr.sharedMaterial = lit;

        PlantController.instance.RegisterSwaying(this);
        UpdateSprite();
    }

    // Cheap deterministic per-plant phase, derived from world coords. Same
    // expression on every load → save/restore is implicit (no need to persist).
    private static float ComputePlantPhase(int x, int y) {
        return (x * 17 + y * 31) * 0.13f;
    }

    // Walks the anchor + every extension SR and writes the current sway MPB
    // values. Must be called any time the plant's tile-height changes (so
    // lower tiles' _PlantHeight stays current) AND any time sprites swap
    // (so each SR's mask-mode flag reflects whether its current sprite has
    // a `_sway.png` companion). Cheap (one MPB read-modify-write per SR), so
    // we don't bother diffing.
    private void RefreshSwayMPB() {
        int height = 1 + extensionSrs.Count;
        string plantName = plantType.name.Replace(" ", "");
        if (sr != null)
            LightReceiverUtil.SetPlantSwayMPB(sr, tile.y, height, plantPhase, HasSwayMaskCompanion(plantName, sr.sprite));
        for (int i = 0; i < extensionSrs.Count; i++) {
            var ex = extensionSrs[i];
            if (ex != null)
                LightReceiverUtil.SetPlantSwayMPB(ex, tile.y, height, plantPhase, HasSwayMaskCompanion(plantName, ex.sprite));
        }
    }

    // Returns true iff a `{texturename}_sway.png` companion exists for the
    // sprite's source texture. The `_SwayMask` secondary texture itself is
    // wired by SpriteNormalMapGenerator's editor-time post-pass and bound by
    // Unity at render time. We can't query secondary textures at runtime in
    // this Unity version, so we use companion-file presence as a proxy:
    // user must run Tools → Generate All Sprite Normal Maps after authoring
    // a `_sway.png` so the secondary binding is created (same workflow as
    // `_n.png` / `_e.png`). Resources.Load caches results internally so the
    // repeated lookups during growth ticks are cheap.
    //
    // Currently HARDCODED to false so all plants stay in vertex-mode (Phase
    // 1/2 height-weighted bend). Mask-mode (Phase 3) shipped but the visual
    // wasn't right for trees yet — re-enable by removing the early return
    // and revisiting authoring. Shader/splitter/wiring infrastructure is
    // all preserved.
    private static bool HasSwayMaskCompanion(string plantName, Sprite s) {
        return false;
        // if (s == null || s.texture == null) return false;
        // return Resources.Load<Sprite>("Sprites/Plants/Split/" + plantName + "/" + s.texture.name + "_sway") != null;
    }

    private void CreateHarvestOverlay() {
        overlayGo = new GameObject("harvest_overlay");
        overlayGo.transform.SetParent(go.transform, false);
        overlayGo.transform.localPosition = Vector3.zero;
        // Unlit layer → renders full-bright via UnlitOverlayCamera, skips LightFeature. See SPEC-rendering.md.
        int unlitLayer = GetUnlitLayer();
        if (unlitLayer >= 0) overlayGo.layer = unlitLayer;
        overlaySr = overlayGo.AddComponent<SpriteRenderer>();
        Material unlitMat = GetUnlitOverlayMaterial();
        if (unlitMat != null) overlaySr.sharedMaterial = unlitMat;
        overlaySr.sprite = GetHarvestOverlaySprite();
        overlaySr.sortingOrder = sr.sortingOrder + 1;
        overlayGo.SetActive(false);
    }

    // Player just finished planting via a blueprint → auto-flag for harvest so the crop
    // gets reaped when ripe without a separate click. Only fires on the gameplay path —
    // worldgen (PlantAt / ScatterPlants) goes through Place() not Construct(), and save
    // load restores the persisted flag — so starter and saved plants are unaffected.
    public override void OnPlaced() {
        SetHarvestFlagged(true);
    }

    // Plants only carry a harvest order while flagged — flipping the flag registers or
    // removes the order so dispatch never has to inspect gated-off orders.
    public void SetHarvestFlagged(bool v) {
        if (harvestFlagged == v) return;
        harvestFlagged = v;
        if (overlayGo != null) overlayGo.SetActive(v);

        WorkOrderManager wom = WorkOrderManager.instance;
        if (wom == null) return;
        if (v) wom.RegisterHarvest(this);
        else   wom.UnregisterHarvest(this);
    }

    public void Grow(int t){
        // Gate 1: ambient temp + soil moisture must be in the plant's comfort range.
        // The plant occupies an air tile; moisture lives on the solid soil tile directly
        // below, so we pass that tile's moisture value. If there is no tile below
        // (world bottom edge) the soil is treated as missing → moisture check skipped.
        // Out-of-range simply freezes growth for the tick — no withering, no stress damage.
        Tile soil = World.instance.GetTileAt(tile.x, tile.y - 1);
        if (!plantType.IsComfortableAt(soil, WeatherSystem.instance)) return;

        // Gate 2: crossing into a new growth stage. max stage = 4 * maxHeight - 1
        // (4 per tile; single-tile plants cap at 3 exactly like before).
        // A crossing always costs 2 × moistureDrawPerHour from the soil. If the
        // crossing also lands us in a new height band (stage / 4 increased), we
        // additionally require every new tile above to be free. Either gate failing
        // freezes age + stage for the tick so they stay coherent.
        int maxStage       = 4 * plantType.maxHeight - 1;
        int candidateAge   = age + t;
        int candidateStage = Math.Min(candidateAge * 3 / plantType.growthTime, maxStage);
        int prevStage      = growthStage;
        if (candidateStage > growthStage) {
            int cost = Mathf.RoundToInt(plantType.moistureDrawPerHour * 2f);
            if (cost > 0 && (soil == null || !soil.type.solid || soil.moisture < cost)) return;

            int candidateHeight = 1 + candidateStage / 4;
            if (candidateHeight > height && !CanExtendTo(candidateHeight)) return;

            if (cost > 0) soil.moisture = (byte)(soil.moisture - cost);
            if (candidateHeight > height) {
                for (int h = height; h < candidateHeight; h++) ClaimExtensionTile(h);
                height = candidateHeight;
            }
        }

        age         = candidateAge;
        growthStage = candidateStage;
        // Harvestable once the plant has reached a "mature" 4-stage segment (stage 3 of any
        // tile). Multi-tile plants keep growing past this — harvest at stage 3 gives 1× yield,
        // stage 7 gives 2×, stage 11 gives 3×. Rewards the player for leaving tall bamboo alone.
        if (growthStage >= 3 && !harvestable){
            harvestable = true;
        }
        // Only rebuild visuals when the growth stage actually changed. The
        // blob-sway path destroys and respawns every child blob GO inside
        // UpdateSprite — running it every tick would reset their localPosition
        // to (0,0,0) for one frame each second, causing a visible synchronized
        // "snap left then back" jitter on every blob in the scene.
        if (growthStage != prevStage) UpdateSprite();
    }
    // Worldgen shortcut: set a plant fully grown without paying the soil moisture
    // advancement cost (fresh worlds don't guarantee a wet soil tile yet).
    // Also claims any upper tiles — but unlike Grow, it SKIPS blocked tiles rather
    // than failing, so a matured plant may end up shorter than maxHeight if the
    // world geometry doesn't allow full extension.
    public void Mature(){
        int maxStage = 4 * plantType.maxHeight - 1;
        age         = plantType.growthTime * maxStage / 3; // keep age coherent with stage
        growthStage = maxStage;
        harvestable = true;
        RebuildExtensionTiles();
    }
    public ItemQuantity[] Harvest(){
        if (!harvestable) { Debug.LogError($"Harvest() called on {plantType.name} but harvestable=false"); return Array.Empty<ItemQuantity>(); }

        // Yield scales linearly with occupied height — a 3-tile bamboo drops 3× the
        // per-tile yield authored in plantsDb.json.
        int scale = Mathf.Max(1, height);
        ItemQuantity[] yields = new ItemQuantity[plantType.products.Length];
        for (int i = 0; i < yields.Length; i++)
            yields[i] = new ItemQuantity(plantType.products[i].item, plantType.products[i].quantity * scale);

        ReleaseAllExtensionTiles();
        harvestable = false;
        age         = 0; // autoreplant
        growthStage = 0;
        UpdateSprite();
        return yields;
    }

    // Called by SaveSystem after restoring age/growthStage/harvestable so the plant's
    // claim on tiles above the anchor is reestablished. Height is re-derived from
    // growthStage rather than persisted — single source of truth.
    public void RebuildExtensionTiles() {
        int targetHeight = Mathf.Clamp(1 + growthStage / 4, 1, plantType.maxHeight);
        ReleaseAllExtensionTiles(); // defensive: clean any prior GOs
        for (int h = 1; h < targetHeight; h++) {
            // If a restore races against some other structure claiming the tile, log
            // so we don't silently overwrite — shouldn't happen with normal save order.
            Tile t = World.instance.GetTileAt(tile.x, tile.y + h);
            if (t == null) {
                Debug.LogError($"Plant.RebuildExtensionTiles: tile above {plantType.name}@{tile.x},{tile.y} is out of bounds at h={h}");
                break;
            }
            if (t.structs[0] != null && t.structs[0] != this) {
                Debug.LogError($"Plant.RebuildExtensionTiles: tile at {t.x},{t.y} occupied by {t.structs[0].structType.name} — aborting extension rebuild at h={h}");
                break;
            }
            ClaimExtensionTile(h);
        }
        height = 1 + extensionSrs.Count;
        UpdateSprite();
        RefreshSwayMPB();
    }

    // True if every tile from y+height..y+targetHeight-1 is free of solid terrain
    // and has a null structs[0]. No blueprints check — a stacked blueprint at depth 0
    // on an empty air tile doesn't block plant extension (the blueprint will re-resolve
    // when the mouse tries to construct onto a now-occupied tile).
    private bool CanExtendTo(int targetHeight) {
        for (int h = height; h < targetHeight; h++) {
            Tile t = World.instance.GetTileAt(tile.x, tile.y + h);
            if (t == null) return false;
            if (t.type.solid) return false;
            if (t.structs[0] != null) return false;
        }
        return true;
    }

    private void ClaimExtensionTile(int h) {
        Tile t = World.instance.GetTileAt(tile.x, tile.y + h);
        t.structs[0] = this;
        t.NotifyStructChanged();

        GameObject extGo = new GameObject($"plant_{plantType.name}_ext{h}");
        extGo.transform.SetParent(go.transform, false);
        extGo.transform.localPosition = new Vector3(0, h, 0);
        SpriteRenderer extSr = SpriteMaterialUtil.AddPlantSpriteRenderer(extGo);
        // Blob-sway plants drive motion via transforms on child blob SRs, not
        // the vertex shader. Every tile-level SR (anchor and extensions) must
        // therefore be on the non-sway lit material or the two systems fight
        // each other (vertex shift + transform shift = visible double-slide).
        if (hasBlobSway) {
            var lit = SpriteMaterialUtil.LitSpriteMaterial;
            if (lit != null) extSr.sharedMaterial = lit;
        }
        extSr.sortingOrder = sr.sortingOrder;
        LightReceiverUtil.SetSortBucket(extSr);
        extensionGos.Add(extGo);
        extensionSrs.Add(extSr);
        RefreshTintableSrs();
        // Plant just got taller — every existing SR's _PlantHeight is now stale.
        RefreshSwayMPB();
    }

    private void ReleaseAllExtensionTiles() {
        for (int h = 1; h < height; h++) {
            Tile t = World.instance.GetTileAt(tile.x, tile.y + h);
            if (t != null && t.structs[0] == this) {
                t.structs[0] = null;
                t.NotifyStructChanged();
            }
        }
        foreach (var g in extensionGos) if (g != null) UnityEngine.Object.Destroy(g);
        extensionGos.Clear();
        extensionSrs.Clear();
        height = 1;
        RefreshTintableSrs();
        // Anchor's _PlantHeight needs to drop back to 1 — otherwise a harvested
        // bamboo that just regrew its anchor would still sway as if 3 tiles tall.
        RefreshSwayMPB();
    }

    // Keeps Structure.tintableSrs (walked by SetTint for the deconstruct red overlay)
    // in sync with the plant's current growth-stage extension SRs. Called whenever
    // extensions are added or removed. Without this, only the anchor SR would tint —
    // tall plants would deconstruct with red anchor + un-tinted upper sections.
    private void RefreshTintableSrs() {
        var arr = new SpriteRenderer[1 + extensionSrs.Count];
        arr[0] = sr;
        for (int i = 0; i < extensionSrs.Count; i++) arr[1 + i] = extensionSrs[i];
        tintableSrs = arr;
    }

    // Renders the anchor + every extension tile. The topmost tile uses the current
    // growth stage's sprite (mod 4 so stages 4..7 re-cycle on the new upper tile).
    // Tiles below the top use the index-4 stalk-continuation sprite. Single-tile
    // plants (maxHeight=1) keep the old behaviour exactly — g0..g3 on the anchor.
    //
    // Anchor sprites: if the plant ships b0..b4 files, the anchor pulls from those
    // (lets trees have a flared base distinct from upper-trunk segments). Otherwise
    // the anchor falls back to g0..g4 — so bamboo and single-tile crops are unchanged.
    public void UpdateSprite(){
        string n = plantType.name.Replace(" ", "");
        int topStageSpriteIdx = growthStage % 4;

        // Anchor: if the plant has extensions, anchor is a lower tile → index 4.
        // Otherwise it's the topmost and uses the live stage sprite directly.
        int anchorIdx = (extensionSrs.Count > 0) ? 4 : topStageSpriteIdx;

        if (hasBlobSway) {
            // Rebuild every blob from scratch. Growth-stage changes happen on
            // the order of seconds (or less, mostly never), so the cost of
            // destroying + re-spawning ~5-8 child SRs per tile is irrelevant
            // next to the simplicity of "static layer + N fresh blob SRs".
            ClearBlobSrs();

            // Anchor tile — try b{idx}_static first, fall back to g{idx}_static.
            string anchorCell;
            Sprite anchorStatic = LoadStaticSprite(n, "b", anchorIdx, out anchorCell);
            sr.sprite = anchorStatic ?? LoadAnchorSprite(n, anchorIdx);
            SpawnBlobsForTile(n, anchorCell, go.transform, sr.sortingOrder);

            // Extension tiles: all but the last use g4; the last (topmost) uses
            // the current stage % 4. Extension tiles always live on the g
            // row — they're upper-tile sprites by definition.
            for (int i = 0; i < extensionSrs.Count; i++) {
                bool isTop = (i == extensionSrs.Count - 1);
                int idx = isTop ? topStageSpriteIdx : 4;
                string extCell;
                Sprite extStatic = LoadStaticSprite(n, "g", idx, out extCell);
                extensionSrs[i].sprite = extStatic ?? LoadStageSprite(n, idx);
                SpawnBlobsForTile(n, extCell, extensionGos[i].transform, extensionSrs[i].sortingOrder);
            }
        } else {
            sr.sprite = LoadAnchorSprite(n, anchorIdx);
            // Extension tiles: all but the last use g4; the last (topmost) uses the
            // current stage % 4.
            for (int i = 0; i < extensionSrs.Count; i++) {
                bool isTop = (i == extensionSrs.Count - 1);
                extensionSrs[i].sprite = LoadStageSprite(n, isTop ? topStageSpriteIdx : 4);
            }
        }

        // Sprites just changed; the per-SR mask-mode flag may have flipped
        // (different growth stages can have different `_sway.png` companions,
        // and the bottom tiles using g4 may have a mask while the top tile
        // doesn't, or vice versa). Re-write the sway MPB to reflect each SR's
        // current secondary-texture state. No-op for blob-sway plants (lit
        // material ignores sway globals) but cheap, so we always run it.
        RefreshSwayMPB();
    }

    // Spawns one child SR per blob for a single tile. The blob set is keyed
    // by cellName ("g0", "b4", …) which matches the metadata file. Sorting
    // order is one above the tile's static SR so blobs render on top of the
    // trunk layer. PlantController.UpdateAllSway translates each blob's
    // transform each frame.
    private void SpawnBlobsForTile(string plantName, string cellName, Transform parent, int tileSortingOrder) {
        var cellMeta = PlantSwayMetaCache.GetCell(plantName, cellName);
        if (cellMeta == null || cellMeta.blobs == null) return;

        for (int i = 0; i < cellMeta.blobs.Length; i++) {
            Sprite blobSprite = Resources.Load<Sprite>(
                "Sprites/Plants/Split/" + plantName + "/" + cellName + "_b" + i);
            if (blobSprite == null) {
                Debug.LogError($"Plant: missing blob sprite {plantName}/{cellName}_b{i} — sway_meta lists {cellMeta.blobs.Length} blob(s) but PNG is gone. Re-bake required.");
                continue;
            }

            GameObject g = new GameObject($"{cellName}_b{i}");
            g.transform.SetParent(parent, false);
            g.transform.localPosition = Vector3.zero;

            SpriteRenderer blobSr = SpriteMaterialUtil.AddSpriteRenderer(g);
            blobSr.sprite       = blobSprite;
            blobSr.sortingOrder = tileSortingOrder + 1;
            LightReceiverUtil.SetSortBucket(blobSr);

            blobGos.Add(g);
            blobs.Add(new BlobRuntime {
                tx       = g.transform,
                sr       = blobSr,
                phase    = cellMeta.blobs[i].phase,
                isStatic = cellMeta.blobs[i].isStatic,
            });
        }
    }

    private void ClearBlobSrs() {
        for (int i = 0; i < blobGos.Count; i++) {
            if (blobGos[i] != null) UnityEngine.Object.Destroy(blobGos[i]);
        }
        blobGos.Clear();
        blobs.Clear();
    }

    // Called every frame by PlantController for plants on the blob-sway path.
    // `signedWind` carries direction (positive = blowing right, per
    // WeatherSystem) and magnitude — already clamped to [-1, +1] by the
    // controller. The swing factor stays in [0, 1] so each blob only leans
    // WITH the wind, never against it; multiplying by signedWind picks the
    // direction. The zero-wind short-circuit lives in PlantController; by
    // the time we get here, there's something to write.
    public void UpdateBlobSway(float t, float signedWind) {
        if (!hasBlobSway || blobs.Count == 0) return;
        float angleT  = (t + plantPhase) * SwaySpeed;
        float ampWind = SwayAmplitudePx * signedWind;

        for (int i = 0; i < blobs.Count; i++) {
            BlobRuntime b = blobs[i];
            if (b.sr == null || b.isStatic) continue;
            float swing = (Mathf.Sin(angleT + b.phase) + 1f) * 0.5f;   // 0..1
            float dxPx  = swing * ampWind;                              // signed pixels
            var lp = b.tx.localPosition;
            lp.x = dxPx * PixelSize;
            b.tx.localPosition = lp;
        }
    }

    private static Sprite LoadStageSprite(string plantName, int stageIdx) {
        Sprite s = Resources.Load<Sprite>("Sprites/Plants/Split/" + plantName + "/g" + stageIdx);
        if (s == null || s.texture == null)
            s = Resources.Load<Sprite>("Sprites/Plants/Split/" + plantName + "/g0");
        if (s == null || s.texture == null)
            s = Resources.Load<Sprite>("Sprites/Plants/default");
        return s;
    }

    // Tries the b{i} variant first (anchor-row sprite — e.g. flared tree base);
    // falls back to g{i} so plants without a dedicated anchor sheet (bamboo,
    // single-tile crops) keep rendering exactly as before.
    private static Sprite LoadAnchorSprite(string plantName, int stageIdx) {
        Sprite s = Resources.Load<Sprite>("Sprites/Plants/Split/" + plantName + "/b" + stageIdx);
        if (s != null && s.texture != null) return s;
        return LoadStageSprite(plantName, stageIdx);
    }

    // Loads the static-layer sprite for a tile in the blob-sway path. Mirrors
    // LoadAnchorSprite's b-then-g fallback so a plant authored without
    // separate anchor art still resolves a sensible cell. The chosen cellName
    // (b{idx} or g{idx}) is reported via `out` so the caller can use it to
    // look up the matching blob set in the metadata cache.
    private static Sprite LoadStaticSprite(string plantName, string preferredPrefix, int stageIdx, out string cellName) {
        cellName = preferredPrefix + stageIdx;
        var s = Resources.Load<Sprite>("Sprites/Plants/Split/" + plantName + "/" + cellName + "_static");
        if (s != null) return s;
        if (preferredPrefix == "b") {
            cellName = "g" + stageIdx;
            s = Resources.Load<Sprite>("Sprites/Plants/Split/" + plantName + "/" + cellName + "_static");
        }
        return s;
    }

    // Unregister + destroy any blob children when the Plant goes away —
    // PlantController would otherwise keep walking dangling references.
    public override void Destroy() {
        if (hasBlobSway) {
            ClearBlobSrs();
            if (PlantController.instance != null) PlantController.instance.UnregisterSwaying(this);
        }
        ReleaseAllExtensionTiles();
        PlantController.instance?.Remove(this);
        base.Destroy();
    }
}



public class PlantType : StructType {
    public ItemNameQuantity[] nproducts {get; set;}
    public ItemQuantity[] products;

    public override Sprite LoadSprite() {
        string n = name.Replace(" ", "");
        Sprite s = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/g0");
        return s != null && s.texture != null ? s : null;
    }

    public int maxSize;
    public int maxYieldPerSize;
    public int harvestProgress;
    public int growthTime;
    public float harvestTime {get; set;}

    // Max tile-height this plant can reach. 1 = stays at the anchor tile (existing behaviour
    // for all pre-bamboo plants). 2+ = dynamically extends upward as growth stage crosses
    // 4-stage thresholds, claiming tiles above. Harvest yield scales linearly with current
    // height at harvest time, so a 3-tile bamboo drops 3× the per-tile yield in products.
    public int maxHeight {get; set;} = 1;
    // public string njob {get; set;}
    // public Job job;

    // ── Comfort range ────────────────────────────────────────────
    // Nullable so old JSON entries without these fields still deserialize — a null bound
    // is treated as "no limit" in IsComfortableAt. Temps are °C (global ambient from
    // WeatherSystem); moisture is the tile's 0–100 soil-wetness percent (Tile.moisture).
    public float? tempMin     {get; set;}
    public float? tempMax     {get; set;}
    public int?   moistureMin {get; set;}
    public int?   moistureMax {get; set;}

    // Passive soil moisture draw per in-game hour from the tile below. The plant also
    // pays 2× this amount from soil to cross into each new growth stage (see Plant.Grow)
    // — the advancement cost is where moisture shortage actually gates growth; the
    // passive draw just drains the soil over time. Default 4; overridable per plant.
    public float moistureDrawPerHour {get; set;} = 2f;

    // Returns true when the current ambient temperature AND the soil moisture are both
    // within this plant's authored comfort range. `soilTile` is the solid tile directly
    // below the plant (where moisture physically lives) — null if the plant is at the
    // world's bottom edge or the tile-below lookup missed, in which case the moisture
    // side is treated as "no data" (check skipped, not failed).
    // WeatherSystem can be null during very-early startup / tests; treat as in-range so
    // plants don't stall before the first tick wires things up.
    public bool IsComfortableAt(Tile soilTile, WeatherSystem weather) {
        if (weather != null) {
            float t = weather.temperature;
            if (tempMin.HasValue && t < tempMin.Value) return false;
            if (tempMax.HasValue && t > tempMax.Value) return false;
        }
        if (soilTile != null) {
            int m = soilTile.moisture;
            if (moistureMin.HasValue && m < moistureMin.Value) return false;
            if (moistureMax.HasValue && m > moistureMax.Value) return false;
        }
        return true;
    }

    [OnDeserialized]
    new internal void OnDeserialized(StreamingContext context){
        costs = ncosts.Select(iq => new ItemQuantity(iq.name, ItemStack.LiangToFen(iq.quantity))).ToArray();
        products = nproducts.Select(iq => new ItemQuantity(iq.name, ItemStack.LiangToFen(iq.quantity))).ToArray();
        if (njob != null){
            job = Db.jobByName[njob];
        }
        // handle null or 0 growthTime?
    }

}