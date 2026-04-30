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

        CreateHarvestOverlay();
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
        UpdateSprite();
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

    public override void Destroy() {
        ReleaseAllExtensionTiles();
        PlantController.instance.Remove(this);
        base.Destroy();
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

        GameObject extGo = new GameObject($"plant_{plantType.name}_ext{h}");
        extGo.transform.SetParent(go.transform, false);
        extGo.transform.localPosition = new Vector3(0, h, 0);
        SpriteRenderer extSr = SpriteMaterialUtil.AddSpriteRenderer(extGo);
        extSr.sortingOrder = sr.sortingOrder;
        LightReceiverUtil.SetSortBucket(extSr);
        extensionGos.Add(extGo);
        extensionSrs.Add(extSr);
    }

    private void ReleaseAllExtensionTiles() {
        for (int h = 1; h < height; h++) {
            Tile t = World.instance.GetTileAt(tile.x, tile.y + h);
            if (t != null && t.structs[0] == this) t.structs[0] = null;
        }
        foreach (var g in extensionGos) if (g != null) UnityEngine.Object.Destroy(g);
        extensionGos.Clear();
        extensionSrs.Clear();
        height = 1;
    }

    // Renders the anchor + every extension tile. The topmost tile uses the current
    // growth stage's sprite (mod 4 so stages 4..7 re-cycle g0..g3 on the new upper
    // tile). Tiles below the top use the `g4` stalk-continuation sprite. Single-tile
    // plants (maxHeight=1) keep the old behaviour exactly — g0..g3 on the anchor.
    public void UpdateSprite(){
        string n = plantType.name.Replace(" ", "");
        int topStageSpriteIdx = growthStage % 4;

        // Anchor: if the plant has extensions, anchor is a lower tile → g4.
        // Otherwise it's the topmost and uses the live stage sprite directly.
        Sprite anchorSprite = (extensionSrs.Count > 0)
            ? LoadStageSprite(n, 4)
            : LoadStageSprite(n, topStageSpriteIdx);
        sr.sprite = anchorSprite;

        // Extension tiles: all but the last use g4; the last (topmost) uses the
        // current stage % 4.
        for (int i = 0; i < extensionSrs.Count; i++) {
            bool isTop = (i == extensionSrs.Count - 1);
            extensionSrs[i].sprite = LoadStageSprite(n, isTop ? topStageSpriteIdx : 4);
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