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

    // Ticks spent at max stage with ripe fruit, for the unpicked-fruit rot timer (see Grow).
    // Wall-clock — advances every tick regardless of growth comfort. Not persisted: a reload
    // restarts the countdown, which is harmless for a cosmetic ~2-day timer.
    private int ripeTicks;

    // Growth-rate multiplier applied when soil moisture is OUTSIDE the plant's comfort
    // range. Out-of-range slows growth to this fraction instead of freezing it (which used
    // to permanently stall fresh-world crops during a dry spell). See Grow's gate 1b.
    private const float DroughtGrowthRate = 0.3f;

    // Fractional growth-progress carry for the soft moisture gate. When growing at
    // DroughtGrowthRate, each tick contributes <1 to age; the remainder accumulates here
    // until it sums to a whole tick. Not persisted — losing <1 tick on reload is harmless.
    private float slowGrowthCarry;

    // ── Sun exposure ──────────────────────────────────────────────────────────
    // Fraction (0..1) of this plant's sun requirement that's met — clamp01(open sky degrees /
    // sunNeedDegrees), from a raycast over the upper hemisphere (see World.OpenSkyDegreesAt).
    // 1 = fully sunlit. A SOFT growth gate (gate 1c): the growth rate scales with it, floored at
    // MinSunGrowthRate so a fully shaded plant still creeps rather than freezing. Cached and
    // recomputed on a throttle (shade only changes when the player builds/mines) plus on any
    // height change; the InfoPanel forces a live recompute for the inspected plant. Not persisted.
    private float sunOpen01 = 1f;
    public  float SunOpen01 => sunOpen01;

    // Min growth-rate multiplier when fully shaded (0° open sky). Floors the sun factor so an
    // enclosed plant keeps inching forward instead of stalling forever — mirrors how the moisture
    // gate floors at DroughtGrowthRate rather than freezing.
    private const float MinSunGrowthRate = 0.2f;

    // Ticks between throttled sun recomputes in Grow. Shade is static between build/mine events,
    // and growth is slow, so a coarse cadence is invisible. Counts down each tick; a height change
    // forces an immediate recompute by zeroing it.
    private const int SunRecomputeInterval = 30;
    private int sunRecomputeTimer;

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

    // True when this plant ships a baked blob-sway set (PlantBlobBaker output) —
    // static-layer main SR + one child SR per blob, swayed by transform. Gates the
    // blob path throughout this class; false keeps the plant on the vertex-sway shader.
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
    // Max displacement in pixels at |wind| = 1. Continuous (sub-pixel); point
    // filtering resolves the fractional transform to crisp pixels.
    private const float SwayAmplitudePx = 1f;
    // Radians per second — one cycle every ~4 seconds. Matches the feel of
    // the previous baked-frame loop.
    private const float SwaySpeed       = Mathf.PI / 2f;
    private const float PixelSize       = 1f / 16f;       // matches Plant PPU

    private GameObject     overlayGo;
    private SpriteRenderer overlaySr;

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
        sr.sortingOrder = 16;
        // Draw in front of buildings (10), grass overlay (11), flowers (12), and platforms
        // (15), but well below the creature band (48+) so a mouse overlapping a plant draws in
        // front. 16 sits inside the 9..17 "buildings" bucket, so the natural sortingOrder→bucket
        // mapping also front-lights the plant like a building (torch-lit, not creature back-lit)
        // — no explicit bucket override needed.
        SortBucketUtil.SetBucketFor(sr);

        // Anchor SR was spawned by StructureVisualBuilder with the standard lit
        // material; swap to the plant-sway variant so wind-vertex displacement
        // applies. Done here rather than via a flag in the shared builder
        // because plants are the only structure type that needs this. Water plants
        // (lilies) keep the plain lit material — they float, so they don't sway.
        var plantMat = plantType.isWaterPlant
            ? SpriteMaterialUtil.LitSpriteMaterial
            : SpriteMaterialUtil.PlantSpriteMaterial;
        if (plantMat != null) sr.sharedMaterial = plantMat;

        plantPhase = ComputePlantPhase(x, y);
        RefreshSwayMPB();

        CreateHarvestOverlay();

        TryEnableBlobSway();
    }

    // Flip the plant onto the blob-sway path if it ships a baked set (sway_meta +
    // per-blob sprites). The anchor SR moves to the non-sway lit material because
    // blob plants drive motion via child transforms, not the vertex shader.
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
        // Blob plants render rigid at the tile level (motion is per-blob transforms),
        // so gate _PlantSway OFF on these SRs — else NormalsCapture re-applies the
        // whole-plant bend to their normals (its vert shifts when _PlantSway > 0.5)
        // and the shading sways as one chunk while the trunk stays put. swayAmount 0
        // also undoes the one ctor RefreshSwayMPB call that runs pre-TryEnableBlobSway.
        float swayAmount = (hasBlobSway || plantType.isWaterPlant) ? 0f : 1f;
        if (sr != null)
            LightReceiverUtil.SetPlantSwayMPB(sr, tile.y, height, plantPhase, HasSwayMaskCompanion(plantName, sr.sprite), swayAmount);
        for (int i = 0; i < extensionSrs.Count; i++) {
            var ex = extensionSrs[i];
            if (ex != null)
                LightReceiverUtil.SetPlantSwayMPB(ex, tile.y, height, plantPhase, HasSwayMaskCompanion(plantName, ex.sprite), swayAmount);
        }
    }

    // Whether the sprite's texture has a `{texturename}_sway.png` companion —
    // used as a runtime proxy for "a `_SwayMask` secondary texture is bound"
    // (we can't query secondary textures directly in this Unity version).
    //
    // Currently HARDCODED to false so all plants stay in vertex-mode (height-
    // weighted bend). Mask-mode (Phase 3) shipped but didn't look right for
    // trees — re-enable by deleting the early return. Infrastructure preserved.
    private static bool HasSwayMaskCompanion(string plantName, Sprite s) {
        return false;
        // if (s == null || s.texture == null) return false;
        // return Resources.Load<Sprite>("Sprites/Plants/Split/" + plantName + "/" + s.texture.name + "_sway") != null;
    }

    private void CreateHarvestOverlay() {
        overlayGo = new GameObject("harvest_overlay");
        overlayGo.transform.SetParent(go.transform, false);
        overlayGo.transform.localPosition = Vector3.zero;
        // Unlit layer → drawn after the lighting composite by UnlitOverlayCamera.
        // The overlay-ambient material dims it toward night ambient so the harvest
        // flag stops glaring in the dark (see UnlitOverlayAmbient.shader). See SPEC-rendering.md.
        int unlitLayer = GetUnlitLayer();
        if (unlitLayer >= 0) overlayGo.layer = unlitLayer;
        overlaySr = overlayGo.AddComponent<SpriteRenderer>();
        Material unlitMat = SpriteMaterialUtil.OverlayAmbientMaterial;
        if (unlitMat != null) overlaySr.sharedMaterial = unlitMat;
        overlaySr.sprite = GetHarvestOverlaySprite();
        overlaySr.sortingOrder = sr.sortingOrder + 1;
        overlaySr.drawMode = SpriteDrawMode.Sliced; // 9-sliced so the flag stretches to the plant's footprint
        RefreshHarvestOverlaySize();
        overlayGo.SetActive(false);
    }

    // Stretches the harvest flag overlay to cover the plant's full footprint (anchor +
    // every extension tile) so multi-tile plants show the flag across their top. The
    // sprite is 9-sliced, so the border holds crisp while the centre stretches. Called
    // on creation and whenever the plant's tile-height changes.
    private void RefreshHarvestOverlaySize() {
        if (overlaySr == null) return;
        int h = 1 + extensionSrs.Count;
        int w = Mathf.Max(1, structType.nx);
        overlaySr.size = new Vector2(w, h);
        // Sliced sprite keeps its centre pivot — recentre on the full footprint so it
        // spans the anchor tile (y) through the top tile (y + h - 1).
        overlayGo.transform.localPosition = new Vector3((w - 1) / 2f, (h - 1) / 2f, 0f);
    }

    // Player just finished planting via a blueprint → auto-flag for harvest so the crop
    // gets reaped when ripe without a separate click. Only fires on the gameplay path —
    // worldgen (PlantAt / ScatterPlants) goes through Place() not Construct(), and save
    // load restores the persisted flag — so starter and saved plants are unaffected.
    public override void OnPlaced() {
        SetHarvestFlagged(true);
        // Standing watering order — self-guards on a moisture-comfort floor and dedups. Worldgen
        // (Place, no OnPlaced) and save-load register theirs via WorkOrderManager.Reconcile instead.
        WorkOrderManager.instance?.RegisterWater(this);
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

    // Pins a water plant's sprite to the surface of the water tile it occupies (height varies
    // with the tile's fill level, 0..1 of the way up) and removes the plant if the water has
    // drained. Returns false when it destroyed the plant, so Grow stops processing it.
    private bool FloatOnWater() {
        float w = tile.water;
        if (w <= 0f) { Destroy(); return false; }
        float frac = Mathf.Min(1f, w / WaterController.WaterMax);
        Vector3 p = go.transform.position;
        go.transform.position = new Vector3(p.x, tile.y + frac, p.z);
        return true;
    }

    // ── Moisture reservoir ─────────────────────────────────────────────────────
    // A plant draws and stores moisture from ONE reservoir, resolved here so growth, transpiration,
    // watering, and the info panel all agree on the source:
    //   • a self-contained greenhouse's isolated pool (built on stone / elevated over air), or
    //   • the soil tile directly below (the common case — bare crops and ground-placed greenhouses).

    // The self-contained greenhouse this plant draws from, or null if it roots in the soil below
    // (ground-mode greenhouse, or a bare crop with no greenhouse at all).
    public Greenhouse SelfContainedPot() {
        return tile.greenhouse is Greenhouse g && g.selfContained ? g : null;
    }

    // The solid soil tile this plant draws from, or null when it draws from a self-contained pot
    // instead (or there's genuinely no soil below — shouldn't happen for a validly-placed plant).
    public Tile SoilTile() {
        if (SelfContainedPot() != null) return null;
        Tile s = World.instance.GetTileAt(tile.x, tile.y - 1);
        return (s != null && s.type.solid) ? s : null;
    }

    // Current moisture in this plant's reservoir (0..MoistureMax), or -1 if it has no reservoir.
    public int ReservoirMoisture() {
        Greenhouse pot = SelfContainedPot();
        if (pot != null) return pot.selfMoisture;
        Tile s = SoilTile();
        return s != null ? s.moisture : -1;
    }

    public bool HasReservoir() => ReservoirMoisture() >= 0;

    // Adds delta moisture (may be negative), clamped 0..MoistureMax. No-op when there's no reservoir.
    public void AddReservoirMoisture(int delta) {
        Greenhouse pot = SelfContainedPot();
        if (pot != null) {
            pot.selfMoisture = (byte)Mathf.Clamp(pot.selfMoisture + delta, 0, MoistureSystem.MoistureMax);
            return;
        }
        Tile s = SoilTile();
        if (s != null) s.moisture = (byte)Mathf.Clamp(s.moisture + delta, 0, s.type.moistureCapacity);
    }

    public void Grow(int t){
        // Water plants (lilies) float at their tile's water surface and die if it drains away.
        // Handled first: a dry tile means the plant is gone this tick, so skip all growth.
        if (plantType.placement == "water" && !FloatOnWater()) return;

        // Unpicked fruit rots on a wall-clock timer that runs BEFORE the comfort gate, so the
        // fruit drops on schedule even when the tree is too cold/dry to grow. Once ripe (max
        // stage) ripeTicks accumulates every tick; past fruitRotTicks the fruit drops (no yield)
        // and the tree rewinds to re-fruit. RewindFruitCycle zeroes ripeTicks; so does dropping
        // below max stage by any other means.
        if (plantType.isFruitTree && plantType.fruitRotTicks > 0 && growthStage >= plantType.maxStage) {
            ripeTicks += t;
            if (ripeTicks >= plantType.fruitRotTicks) { RewindFruitCycle(); return; }
        } else {
            ripeTicks = 0;
        }

        // Moisture comes from this plant's reservoir — the soil tile below (bare crop or ground
        // greenhouse) OR a self-contained greenhouse's isolated pool. -1 = no reservoir (no soil,
        // no pool), treated as "no data" so a misplaced plant doesn't wither. The reservoir is a
        // physical fact independent of greenhouse condition — a broken greenhouse still holds its pool.
        int rm = ReservoirMoisture();

        // A greenhouse frame covering the anchor tile regulates the climate: it warms the
        // temperature gate toward its target (gate 1a), speeds growth (gate 1b), halves the
        // stage moisture cost (gate 2), and caps height (gate 3, via CanExtendTo). The season
        // gate is intentionally NOT regulated — herbs are wild-only and can't be planted here.
        // A broken greenhouse provides no climate regulation until repaired — treat it as absent
        // for all bonuses. The height cap (CanExtendTo) still reads tile.greenhouse directly, since
        // the glass frame physically blocks regardless of condition.
        Structure ghStruct = tile.greenhouse;
        StructType gh = (ghStruct != null && !ghStruct.IsBroken) ? ghStruct.structType : null;

        // Gate 1a: temperature is a HARD gate — out of comfort range freezes growth for the
        // tick. A greenhouse pulls ambient a fraction of the way toward its target temp before
        // the check (e.g. halfway toward ~80°F), so it warms a cold day without being perfect —
        // leaving room for stronger greenhouses to push the pull/target higher.
        WeatherSystem weather = WeatherSystem.instance;
        if (weather != null) {
            float airTemp = gh != null ? gh.RegulatedTemp(weather.temperature) : weather.temperature;
            if (!plantType.IsTempComfortableAtTemp(airTemp)) return;
        }

        // Gate 1a': season is also a HARD gate. Most herbs use this instead of a temp
        // window — out-of-season freezes growth (e.g. a fall-only chrysanthemum sits
        // dormant the rest of the year). Year-round plants leave `seasons` null.
        if (!plantType.IsSeasonComfortableAt(WeatherSystem.instance)) return;

        // Gate 1b: moisture is a SOFT gate — out of comfort range slows growth to
        // DroughtGrowthRate rather than freezing it, so a fresh world that hasn't rained
        // yet still inches its crops along instead of stalling forever. Sub-tick progress
        // accumulates in slowGrowthCarry until it sums to a whole tick (age is integral).
        float rate = plantType.IsMoistureComfortableForLevel(rm) ? 1f : DroughtGrowthRate;
        if (gh != null) rate *= gh.greenhouseGrowthMult;

        // Gate 1c: sun exposure is a SOFT gate — overhead shade scales growth down linearly,
        // floored at MinSunGrowthRate so a shaded plant still creeps. Recomputed on a throttle
        // (and on height change, which zeroes the timer). See sunOpen01 / RecomputeSunExposure.
        if (--sunRecomputeTimer <= 0) RecomputeSunExposure();
        rate *= Mathf.Lerp(MinSunGrowthRate, 1f, sunOpen01);

        slowGrowthCarry += t * rate;
        int effectiveT   = (int)slowGrowthCarry;
        slowGrowthCarry -= effectiveT;
        if (effectiveT <= 0) return;

        // Gate 2: crossing into a new growth stage. max stage = growthStages * maxHeight - 1
        // (growthStages per tile; single-tile default-4 plants cap at 3 exactly like before).
        // A crossing always costs stageMoistureCost from the soil. If the crossing also
        // lands us in a new height band (stage / growthStages increased), we additionally
        // require every new tile above to be free. Either gate failing freezes age + stage
        // for the tick so they stay coherent.
        int maxStage       = plantType.maxStage;
        int candidateAge   = age + effectiveT;
        int candidateStage = Math.Min(candidateAge * plantType.stageSpan / plantType.growthTime, maxStage);
        int prevStage      = growthStage;
        if (candidateStage > growthStage) {
            // A greenhouse's controlled humidity cuts how much moisture each stage crossing spends
            // (gate 2) — same multiplier that trims passive transpiration in MoistureSystem. Spent
            // from the reservoir (soil below or self-contained pool); rm < cost (incl. the no-reservoir
            // rm = -1 case) freezes the crossing.
            int cost = plantType.stageMoistureCost;
            if (gh != null) cost = Mathf.RoundToInt(cost * gh.greenhouseMoistureMult);
            if (cost > 0 && rm < cost) return;

            int candidateHeight = HeightForStage(candidateStage);
            if (candidateHeight > height && !CanExtendTo(candidateHeight)) return;

            if (cost > 0) AddReservoirMoisture(-cost);
            if (candidateHeight > height) {
                for (int h = height; h < candidateHeight; h++) ClaimExtensionTile(h);
                height = candidateHeight;
            }
        }

        age         = candidateAge;
        growthStage = candidateStage;
        // Harvestable once the plant completes its first tile's stages (stage growthStages-1).
        // Multi-tile plants keep growing past this; the harvest work order stays dormant until
        // IsDoneGrowing(), so trees/bamboo are reaped at full height and deliver their height-
        // scaled yield. For single-tile plants that threshold IS maxStage. `harvestable` here
        // only gates harvest sanity rechecks.
        if (growthStage >= plantType.growthStages - 1 && !harvestable){
            harvestable = true;
        }
        // Only rebuild visuals when the growth stage actually changed. The
        // blob-sway path destroys and respawns every child blob GO inside
        // UpdateSprite — running it every tick would reset their localPosition
        // to (0,0,0) for one frame each second, causing a visible synchronized
        // "snap left then back" jitter on every blob in the scene.
        if (growthStage != prevStage) UpdateSprite();
    }

    // Rewind a fruit tree to its first fruitless mature stage — drops the fruit but keeps the
    // tree standing, so only the final fruiting sub-cycle replays. Shared by Harvest (after
    // computing yield) and the unpicked-fruit rot timer in Grow.
    private void RewindFruitCycle(){
        int target  = Mathf.Max(0, plantType.maxStage - plantType.fruitCycleStages);
        growthStage = target;
        age         = target * plantType.growthTime / plantType.stageSpan;
        ripeTicks   = 0;
        UpdateSprite();
    }
    // Tile-height this plant occupies at a given growth stage. Table-driven plants read it
    // from growthFrames (each entry lists one sprite per occupied tile); others use the
    // growthStages-per-tile formula. Single source of truth for the height the growth/extension
    // logic claims and the save/load rebuild re-derives.
    private int HeightForStage(int stage) {
        if (plantType.hasGrowthTable) {
            var frames = plantType.growthFrames;
            stage = Mathf.Clamp(stage, 0, frames.Length - 1);
            return Mathf.Max(1, frames[stage].Length);
        }
        // Clamp to maxHeight: within the valid stage range [0, maxStage] the formula never
        // exceeds maxHeight, but a stale/out-of-range growthStage (e.g. a plant saved under a
        // different growthStages, or pre-migration data) must not claim a phantom tile above.
        return Mathf.Min(plantType.maxHeight, 1 + stage / plantType.growthStages);
    }

    // Worldgen shortcut: jump a plant straight to a given growth stage without paying the
    // per-stage soil-moisture cost (fresh worlds don't guarantee wet soil yet). Claims any
    // upper tiles for that stage — but unlike Grow it SKIPS blocked tiles rather than failing
    // (via RebuildExtensionTiles), so the plant may end up shorter if geometry blocks it.
    public void Mature(int stage){
        stage       = Mathf.Clamp(stage, 0, plantType.maxStage);
        growthStage = stage;
        age         = plantType.growthTime * stage / plantType.stageSpan; // keep age coherent with stage
        harvestable = stage >= plantType.growthStages - 1;                // matches Grow's harvestable gate
        RebuildExtensionTiles();
    }

    // Fully mature (max growth stage) — starter plants and any ready-to-harvest placement.
    public void Mature() => Mature(plantType.maxStage);

    // True when the plant will not grow any taller — either it reached its max
    // growth stage, or it's frozen at the top of its current height band because
    // the tile above is blocked. Gates the harvest work order so multi-tile plants
    // are reaped at full height (delivering their height-scaled yield) instead of
    // the instant they first become harvestable.
    public bool IsDoneGrowing() {
        if (growthStage >= plantType.maxStage) return true;
        // Frozen-blocked: the next stage wants a taller plant but the tile above is
        // unavailable, so growth can't advance. Equivalent to the old `growthStage ==
        // 4*height-1` band-top test for formula plants, and correct for table plants
        // whose height steps don't fall on 4-stage boundaries.
        int nextHeight = HeightForStage(growthStage + 1);
        return nextHeight > height && !CanExtendTo(nextHeight);
    }

    // Recompute cached sun exposure from a sky raycast at the plant's TOP occupied tile (so a
    // taller plant sees over low obstructions). sunOpen01 = clamp01(open sky degrees / need),
    // and the throttle timer is reset. Cheap (~12 short rays); called on a cadence from Grow,
    // forced on height change, and forced live by the InfoPanel for the inspected plant.
    public void RecomputeSunExposure() {
        int topY = tile.y + height - 1;
        float openDeg = World.instance.OpenSkyDegreesAt(tile.x, topY);
        float need = plantType.sunNeedDegrees;
        sunOpen01 = need > 0f ? Mathf.Clamp01(openDeg / need) : 1f;
        sunRecomputeTimer = SunRecomputeInterval;
    }

    // ── Growth-block diagnostics ──────────────────────────────────────────────
    // Why a plant isn't advancing, surfaced in the InfoPanel. Frozen reasons halt
    // growth entirely; the Slow* reasons only throttle it (DroughtGrowthRate). The
    // model reports the cause; StructureInfoView owns the player-facing wording.
    public enum GrowthBlock {
        None,          // growing normally, or already fully grown
        TooCold,       // temperature below tempMin (hard freeze)
        TooHot,        // temperature above tempMax (hard freeze)
        OutOfSeason,   // current season not in `seasons` (hard freeze)
        NoSpaceAbove,  // next stage needs a taller plant but tile(s) above are blocked (hard freeze)
        SoilTooDry,    // soil below stageMoistureCost — can't pay a stage crossing (hard stall)
        SlowDry,       // soil below moisture comfort — only slowed
        SlowWet,       // soil above moisture comfort — only slowed
    }

    // The current reason this plant's growth is impeded, or None if it's growing
    // normally or already fully grown. Mirrors the gate order in Grow() so the
    // reported cause matches the gate that actually stops advancement. Read-only
    // diagnostic — the sim never branches on it.
    public GrowthBlock GetGrowthBlock() {
        if (growthStage >= plantType.maxStage) return GrowthBlock.None; // fully grown — done, not blocked

        WeatherSystem weather = WeatherSystem.instance;
        // A greenhouse regulates the climate it presents to the plant — the verdict must read the
        // SAME regulated temperature and reduced moisture cost as Grow() and the comfort bar, or it
        // reports a stale "too cold" while the plant is actually growing in the warmed interior. A
        // broken greenhouse stops regulating (matches Grow), so exclude it.
        Structure ghStruct = tile.greenhouse;
        StructType gh = (ghStruct != null && !ghStruct.IsBroken) ? ghStruct.structType : null;
        int rm = ReservoirMoisture();   // soil below or self-contained pool; -1 = no reservoir

        // HARD gates first (these freeze growth), in Grow()'s order.
        if (weather != null) {
            float t = gh != null ? gh.RegulatedTemp(weather.temperature) : weather.temperature;
            if (!plantType.IsTempComfortableAtTemp(t)) {
                bool tooHot = plantType.tempMax.HasValue && t > plantType.tempMax.Value;
                return tooHot ? GrowthBlock.TooHot : GrowthBlock.TooCold;
            }
        }
        if (!plantType.IsSeasonComfortableAt(weather)) return GrowthBlock.OutOfSeason;

        // Reservoir too dry to pay a stage crossing — a hard stall (Grow returns at the cost gate
        // before advancing). Checked before the open-sky gate to match Grow's order. The greenhouse
        // halves the stage cost, so mirror that here too.
        int stageCost = plantType.stageMoistureCost;
        if (gh != null) stageCost = Mathf.RoundToInt(stageCost * gh.greenhouseMoistureMult);
        if (stageCost > 0 && rm < stageCost) return GrowthBlock.SoilTooDry;

        // "Open sky" gate: the next stage would make the plant taller, but the tile(s)
        // directly above are solid or occupied, so it can't extend. Only multi-tile
        // plants (trees, bamboo) ever reach this; single-tile crops never need space above.
        int nextHeight = HeightForStage(growthStage + 1);
        if (nextHeight > height && !CanExtendTo(nextHeight)) return GrowthBlock.NoSpaceAbove;

        // Soft moisture gate — out of comfort only slows growth (it doesn't freeze).
        if (rm >= 0 && !plantType.IsMoistureComfortableForLevel(rm)) {
            bool tooWet = plantType.moistureMax.HasValue && rm > plantType.moistureMax.Value;
            return tooWet ? GrowthBlock.SlowWet : GrowthBlock.SlowDry;
        }

        return GrowthBlock.None;
    }

    public ItemQuantity[] Harvest(){
        if (!harvestable) { Debug.LogError($"Harvest() called on {plantType.name} but harvestable=false"); return Array.Empty<ItemQuantity>(); }

        // Yield scales linearly with occupied height — a 3-tile bamboo drops 3× the
        // per-tile yield authored in plantsDb.json.
        int scale = Mathf.Max(1, height);
        ItemQuantity[] yields = new ItemQuantity[plantType.products.Length];
        for (int i = 0; i < yields.Length; i++)
            yields[i] = new ItemQuantity(plantType.products[i].item, plantType.products[i].quantity * scale);

        if (plantType.isFruitTree) {
            // Drop the fruit but stay standing; only the final fruiting sub-cycle replays. The
            // WOM harvest order goes dormant (IsDoneGrowing false) until it regrows to maxStage.
            // harvestable stays true — still a mature tree; dispatch is gated by IsDoneGrowing.
            RewindFruitCycle();
        } else if (plantType.isWild) {
            // Foraging a wild herb removes it; WildHerbSystem re-seeds the world elsewhere.
            // Safe to Destroy here: HarvestTask reserves nothing and its remaining objectives
            // operate on the produced items, not this plant.
            Destroy();
        } else {
            // Felled crop (wheat/rice/soybean/pine): harvest takes the whole plant down. Destroy it
            // and drop a replant blueprint of the same type on the tile, so a farmer re-sows it — the
            // seed is supplied from the harvest yield via the blueprint's normal ncost. The rebuilt
            // plant defaults to harvestFlagged in OnPlaced, inheriting the flag that got it harvested
            // here in the first place.
            Tile plantTile  = tile;
            StructType type = plantType;
            Destroy();
            // Tilling is per-cycle: revert the soil below from "dirttilled" back to plain dirt so the
            // replant has to be re-tilled (guarded so it only touches till-requiring crops' farmland —
            // other felled crops never tilled it). The replant blueprint below registers suspended on
            // the now-untilled dirt and auto-queues a fresh Till order; the harvesting farmer, standing
            // here, picks up that distance-0 Till order immediately, then plants — one continuous stint.
            Tile soil = World.instance?.GetTileAt(plantTile.x, plantTile.y - 1);
            if (soil != null && soil.tilled) soil.type = Db.tileTypeByName["dirt"];
            // Drop the now-stale harvest/water orders keyed to this tile (the plant they pointed at
            // is gone); the replant's Plant.OnPlaced re-registers fresh ones once it's built.
            WorkOrderManager.instance?.RemoveForTile(plantTile);
            // Seed the replant straight from this harvest's yield so the seed never round-trips to
            // storage (and can't be hauled off before the farmer returns to plant). PreDeliver moves
            // one seed's worth into the blueprint and trims it out of `yields`, so the rest drops as
            // normal. The farmer then only needs to till (if required) and plant — supply is done.
            Blueprint replant = new Blueprint(type, plantTile.x, plantTile.y);
            replant.PreDeliver(yields);
        }
        return yields;
    }

    // Items dropped when this plant is REMOVED (chopped down), distinct from Harvest().
    // A fully grown plant yields the full height-scaled removalProducts (what a harvest would
    // drop, unless overridden — e.g. apple → apple wood). Wood trees (fractionalRemoval) also
    // yield a penalised amount while immature: removalProducts × maxHeight × (growthStage/maxStage)
    // × 0.5. Other plants yield nothing until fully grown.
    public ItemQuantity[] RemovalYield(){
        var products = plantType.removalProducts;
        if (products == null || products.Length == 0) return Array.Empty<ItemQuantity>();
        int maxStage = plantType.maxStage;
        float scale;
        if (growthStage >= maxStage)          scale = Mathf.Max(1, height);
        else if (plantType.fractionalRemoval) scale = plantType.maxHeight * (growthStage / (float)maxStage) * 0.5f;
        else                                  scale = 0f;
        if (scale <= 0f) return Array.Empty<ItemQuantity>();
        var yields = new List<ItemQuantity>(products.Length);
        foreach (var p in products) {
            int q = Mathf.RoundToInt(p.quantity * scale);
            if (q > 0) yields.Add(new ItemQuantity(p.item, q));
        }
        return yields.ToArray();
    }

    // Called by SaveSystem after restoring age/growthStage/harvestable so the plant's
    // claim on tiles above the anchor is reestablished. Height is re-derived from
    // growthStage rather than persisted — single source of truth.
    public void RebuildExtensionTiles() {
        int targetHeight = HeightForStage(growthStage);
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
    //
    // Greenhouse cap: a plant rooted inside a greenhouse can't grow past the frame. Each new
    // tile must be covered by the SAME greenhouse instance as the anchor — so a tree in a 1-high
    // frame freezes at height 1, in a 2-high frame at height 2. Comparing the instance (not just
    // non-null) avoids "tunnelling" from one greenhouse up into a separate one stacked above.
    private bool CanExtendTo(int targetHeight) {
        Structure anchorGreenhouse = tile.greenhouse;
        for (int h = height; h < targetHeight; h++) {
            Tile t = World.instance.GetTileAt(tile.x, tile.y + h);
            if (t == null) return false;
            if (t.type.solid) return false;
            if (t.structs[0] != null) return false;
            if (anchorGreenhouse != null && t.greenhouse != anchorGreenhouse) return false;
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
        SortBucketUtil.SetBucketFor(extSr); // shares the anchor's buildings-bucket order/lighting
        extensionGos.Add(extGo);
        extensionSrs.Add(extSr);
        RefreshTintableSrs();
        RefreshHarvestOverlaySize(); // plant grew a tile — stretch the flag to match
        // Plant just got taller — every existing SR's _PlantHeight is now stale.
        RefreshSwayMPB();
        sunRecomputeTimer = 0; // top tile moved up — re-sample sky next Grow
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
        RefreshHarvestOverlaySize(); // collapsed back to anchor — shrink the flag
        // Anchor's _PlantHeight needs to drop back to 1 — otherwise a harvested
        // bamboo that just regrew its anchor would still sway as if 3 tiles tall.
        RefreshSwayMPB();
        sunRecomputeTimer = 0; // collapsed to anchor — re-sample sky next Grow
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

        // Data-driven growth table: each stage names the sprite cell for every occupied tile
        // (anchor first), bypassing the 4-stages-per-tile cycling entirely. Lets fruit trees
        // author a bespoke base→canopy→blossom→fruit sequence. Table plants may also ship a
        // baked blob set — when they do, each frame cell routes through its static layer + a
        // fresh set of blob SRs (same as the non-table blob-sway branch below), using the
        // explicit per-tile cell names the frame already gives us.
        if (plantType.hasGrowthTable) {
            string[] frame = plantType.growthFrames[Mathf.Clamp(growthStage, 0, plantType.growthFrames.Length - 1)];

            if (hasBlobSway) {
                ClearBlobSrs();
                string anchorCell = frame.Length > 0 ? frame[0] : null;
                sr.sprite = LoadCellStatic(n, anchorCell) ?? LoadCellSprite(n, anchorCell) ?? LoadStageSprite(n, 0);
                SpawnBlobsForTile(n, anchorCell, go.transform, sr.sortingOrder);

                for (int i = 0; i < extensionSrs.Count; i++) {
                    string cell = (i + 1 < frame.Length) ? frame[i + 1] : null;
                    extensionSrs[i].sprite = LoadCellStatic(n, cell) ?? LoadCellSprite(n, cell) ?? LoadStageSprite(n, 4);
                    SpawnBlobsForTile(n, cell, extensionGos[i].transform, extensionSrs[i].sortingOrder);
                }
            } else {
                sr.sprite = LoadCellSprite(n, frame.Length > 0 ? frame[0] : null) ?? LoadStageSprite(n, 0);
                for (int i = 0; i < extensionSrs.Count; i++) {
                    string cell = (i + 1 < frame.Length) ? frame[i + 1] : null;
                    extensionSrs[i].sprite = LoadCellSprite(n, cell) ?? LoadStageSprite(n, 4);
                }
            }
            RefreshSwayMPB();
            return;
        }

        int topStageSpriteIdx = growthStage % plantType.growthStages;

        // Anchor: if the plant has extensions, anchor is a lower tile → stalk-continuation
        // index (growthStages). Otherwise it's the topmost and uses the live stage sprite.
        int anchorIdx = (extensionSrs.Count > 0) ? plantType.growthStages : topStageSpriteIdx;

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

            // Extension tiles: all but the last use the stalk-continuation sprite (g{growthStages});
            // the last (topmost) uses the current stage % growthStages. Extension tiles always
            // live on the g row — they're upper-tile sprites by definition.
            for (int i = 0; i < extensionSrs.Count; i++) {
                bool isTop = (i == extensionSrs.Count - 1);
                int idx = isTop ? topStageSpriteIdx : plantType.growthStages;
                string extCell;
                Sprite extStatic = LoadStaticSprite(n, "g", idx, out extCell);
                extensionSrs[i].sprite = extStatic ?? LoadStageSprite(n, idx);
                SpawnBlobsForTile(n, extCell, extensionGos[i].transform, extensionSrs[i].sortingOrder);
            }
        } else {
            sr.sprite = LoadAnchorSprite(n, anchorIdx);
            // Extension tiles: all but the last use the stalk-continuation sprite (g{growthStages});
            // the last (topmost) uses the current stage % growthStages.
            for (int i = 0; i < extensionSrs.Count; i++) {
                bool isTop = (i == extensionSrs.Count - 1);
                extensionSrs[i].sprite = LoadStageSprite(n, isTop ? topStageSpriteIdx : plantType.growthStages);
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
            Sprite blobSprite = PlantBlobSpriteCache.Get(plantName, cellName + "_b" + i);
            if (blobSprite == null) {
                Debug.LogError($"Plant: missing blob sprite {cellName}_b{i} in {plantName}_blobs_baked — sway_meta lists {cellMeta.blobs.Length} blob(s) but the sheet slice is gone. Re-bake required.");
                continue;
            }

            GameObject g = new GameObject($"{cellName}_b{i}");
            g.transform.SetParent(parent, false);
            g.transform.localPosition = Vector3.zero;

            SpriteRenderer blobSr = SpriteMaterialUtil.AddSpriteRenderer(g);
            blobSr.sprite       = blobSprite;
            blobSr.sortingOrder = tileSortingOrder + 1; // 17 — top of the buildings bucket
            SortBucketUtil.SetBucketFor(blobSr);

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

    // Loads a sprite by its exact split-cell name (e.g. "b4", "g0"). Returns null if the cell
    // is missing/empty so the data-driven growth path can fall back. Resources.Load caches.
    private static Sprite LoadCellSprite(string plantName, string cell) {
        if (string.IsNullOrEmpty(cell)) return null;
        Sprite s = Resources.Load<Sprite>("Sprites/Plants/Split/" + plantName + "/" + cell);
        return (s != null && s.texture != null) ? s : null;
    }

    // Static-layer sprite for a blob-sway cell, by exact cell name ("b4", "g0").
    // Symmetric to LoadCellSprite — used by the growth-table blob path where the
    // frame supplies full cell names (so the prefix+stageIdx LoadStaticSprite
    // doesn't fit). Returns null if absent so callers fall back to the full cell.
    private static Sprite LoadCellStatic(string plantName, string cell) {
        if (string.IsNullOrEmpty(cell)) return null;
        return PlantBlobSpriteCache.Get(plantName, cell + "_static");
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
        var s = PlantBlobSpriteCache.Get(plantName, cellName + "_static");
        if (s != null) return s;
        if (preferredPrefix == "b") {
            cellName = "g" + stageIdx;
            s = PlantBlobSpriteCache.Get(plantName, cellName + "_static");
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

    // Items dropped when this plant is REMOVED (chopped down), as opposed to harvested.
    // Authored in liang like nproducts. Null in JSON → defaults to the harvest products,
    // so "remove a mature plant" yields what a harvest would unless overridden (apple → wood).
    public ItemNameQuantity[] nremovalProducts {get; set;}
    public ItemQuantity[] removalProducts;

    // When true, removing this plant before full growth still yields removalProducts,
    // scaled down by growth (see Plant.RemovalYield). Wood trees set this; crops leave it
    // false so an immature crop yields nothing on removal.
    public bool fractionalRemoval {get; set;} = false;

    // Fruit-tree harvest behaviour. 0 = normal: harvest fells the plant (reset to stage 0,
    // release upper tiles). >0 = harvest rewinds this many growth stages and leaves the
    // plant standing, so only the final fruiting sub-cycle replays. Rewind target is
    // (maxStage - fruitCycleStages), which should be the first fruitless mature frame.
    public int fruitCycleStages {get; set;} = 0;
    public bool isFruitTree => fruitCycleStages > 0;

    // Unpicked fruit rots: once ripe (max stage), a fruit tree left unharvested this many ticks
    // drops its fruit (no yield) and rewinds to re-fruit, so it never sits ripe forever. Ticks,
    // matching growthTime (World.ticksInDay = 240). 0 = never rots. Only meaningful when isFruitTree.
    public int fruitRotTicks {get; set;} = 0;

    public override Sprite LoadSprite() {
        string n = name.Replace(" ", "");
        Sprite s = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/g0");
        return s != null && s.texture != null ? s : null;
    }

    // Build-menu icon: the tallest single-tile growth stage — a mature crop / young tree —
    // rather than the g0 seedling LoadSprite returns. Table-driven plants scan growthFrames
    // for the highest stage that still occupies one tile and use that cell; formula plants use
    // the last stage before they'd claim a second tile (index growthStages-1), matching what
    // UpdateSprite renders there. Prefers the b{i} anchor art, then g{i}, then the seedling.
    public Sprite LoadMenuIconSprite() {
        string n = name.Replace(" ", "");
        if (hasGrowthTable) {
            for (int s = growthFrames.Length - 1; s >= 0; s--) {
                if (growthFrames[s].Length != 1) continue;
                Sprite cell = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/" + growthFrames[s][0]);
                if (cell != null && cell.texture != null) return cell;
            }
        } else {
            int idx = growthStages - 1;
            Sprite b = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/b" + idx);
            if (b != null && b.texture != null) return b;
            Sprite g = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/g" + idx);
            if (g != null && g.texture != null) return g;
        }
        return LoadSprite();
    }

    public int maxSize;
    public int maxYieldPerSize;
    public int harvestProgress;
    public int growthTime;
    public float harvestTime {get; set;}

    // Field crop that needs tilled soil: can only be planted on dirt, and a farmer must till
    // the tile first (the construct order stays gated until tile.tilled). Set for wheat/rice/
    // soybean in plantsDb.json. Tilled soil is persistent, so a replant reuses it without re-tilling.
    public bool requiresTill {get; set;} = false;

    // Max tile-height this plant can reach. 1 = stays at the anchor tile (existing behaviour
    // for all pre-bamboo plants). 2+ = dynamically extends upward as growth stage crosses
    // 4-stage thresholds, claiming tiles above. Harvest yield scales linearly with current
    // height at harvest time, so a 3-tile bamboo drops 3× the per-tile yield in products.
    public int maxHeight {get; set;} = 1;

    // Optional data-driven growth table. Each entry is one growth stage, listing the sprite
    // cell for every occupied tile from the anchor (bottom) up — e.g. ["b4","g0"] is a 2-tile
    // stage. When present it fully overrides the implicit "4 stages per tile, b{i}/g{i%4}"
    // model: maxStage, per-stage height, and per-stage sprites all come from this table. Lets
    // fruit trees author a bespoke sequence (base grows, then canopy, then flower, then fruit)
    // on a fixed tile-height. Null → the legacy formula below. See Plant.HeightForStage / UpdateSprite.
    public string[][] growthFrames {get; set;}
    public bool hasGrowthTable => growthFrames != null && growthFrames.Length > 0;

    // Growth-stage sprites per tile: a plant renders g0..g{growthStages-1}. Default 4 (the
    // legacy model — single-tile plants run g0..g3). Herbs set 3 for a tidy g0/g1/g2 lifecycle.
    // Drives maxStage, the per-tile height step, the age↔stage conversion, and which stage
    // sprite renders. Table-driven plants (growthFrames) ignore this — their stage count is the
    // table length.
    public int growthStages {get; set;} = 4;
    // Stages gained over one growthTime span (= growthStages-1, since stage 0 is the seedling).
    // The age↔stage conversions divide by this; guarded ≥1 so a degenerate 1-stage plant can't
    // divide by zero.
    public int stageSpan => Math.Max(1, growthStages - 1);

    // Highest growth stage this plant type reaches. Table-driven plants run the full table;
    // others use growthStages per height tile (single-tile, default 4 → caps at 3 as before).
    public int maxStage => hasGrowthTable ? growthFrames.Length - 1 : growthStages * maxHeight - 1;

    // Relative weight used by WorldGen.ScatterPlants to pick which plant type seeds
    // each natural cluster. Sampled proportionally against all other plant types with
    // genWeight > 0 — units are unnormalized. 0 (default) = never spawns naturally
    // (legacy types, crops planted only by the player). Tune in plantsDb.json.
    public float genWeight {get; set;} = 0f;

    // ── Wild herb lifecycle (WildHerbSystem) ─────────────────────────────────
    // Per-world live-population cap for a WILD herb. >0 marks this type as wild: the world
    // spawns/maintains up to maxWild of them (WildHerbSystem), foraging destroys them instead
    // of auto-replanting (Plant.Harvest), and WorldGen.ScatterPlants skips them. 0 (default) =
    // a normal crop/plant. genWeight still weights WHICH under-cap wild type spawns next.
    public int maxWild {get; set;} = 0;
    public bool isWild => maxWild > 0;

    // Terrain kind a wild herb spawns on: "meadow" (surface dirt, default) or "water" (floats
    // on the topmost water tile, e.g. moonlily). Only read for wild types. Future: "cave", "shade".
    public string placement {get; set;} = "meadow";
    // Floats on water rather than rooting in soil: no wind sway (not anchored to ground), no
    // soil-moisture coupling, and Plant.FloatOnWater pins it to the surface. See Plant.Grow.
    public bool isWaterPlant => placement == "water";
    // public string njob {get; set;}
    // public Job job;

    // ── Comfort range ────────────────────────────────────────────
    // Nullable so old JSON entries without these fields still deserialize — a null bound
    // is treated as "no limit" in IsTempComfortableAt / IsMoistureComfortableAt. Temps are
    // °C (global ambient from WeatherSystem); moisture is the tile's 0–100 soil-wetness percent.
    public float? tempMin     {get; set;}
    public float? tempMax     {get; set;}
    public int?   moistureMin {get; set;}
    public int?   moistureMax {get; set;}

    // Degrees of open sky (of the 180° overhead hemisphere) this plant needs for FULL sun.
    // The growth rate scales with clamp01(open sky degrees / sunNeedDegrees), floored at a
    // minimum so deep shade slows rather than freezes (see Plant sun exposure / Grow gate 1c).
    // Default 90 = "needs roughly half the sky open." Lower = more shade-tolerant. A future
    // shade-loving plant could set this small. See World.OpenSkyDegreesAt.
    public float sunNeedDegrees {get; set;} = 90f;

    // Seasons this plant grows in — each entry is a season name ("Spring"/"Summer"/
    // "Fall"/"Winter") matching WeatherSystem.GetSeason(). Null/empty = year-round
    // (the default for crops). A HARD growth gate like tempMin/tempMax: out-of-season
    // freezes growth for the tick. Easier to author than a temperature window when the
    // intent is "this herb only appears in autumn." WildHerbSystem will read this to
    // gate seasonal spawning too.
    public string[] seasons {get; set;}

    // Passive soil moisture draw per in-game hour from the tile below. Drains the soil
    // over time independent of growth. Default 2 (crops dry out fast enough that hand-
    // watering by an assigned farmer matters); overridable per plant.
    public float moistureDrawPerHour {get; set;} = 2f;

    // Moisture deducted from the soil tile below each time the plant crosses into a new
    // growth stage. This advancement cost is where moisture shortage actually gates growth.
    // Decoupled from moistureDrawPerHour so the two can be tuned independently. Default 4.
    public int stageMoistureCost {get; set;} = 4;

    // Temperature comfort — true when ambient temp is within the authored range.
    // A HARD gate in Plant.Grow: out of range freezes growth entirely. Null bound =
    // no limit. WeatherSystem can be null during very-early startup / tests; treat as
    // in-range so plants don't stall before the first tick wires things up.
    public bool IsTempComfortableAt(WeatherSystem weather) {
        if (weather == null) return true;
        return IsTempComfortableAtTemp(weather.temperature);
    }

    // Comfort check against an explicit temperature, used when something regulates the local
    // climate away from raw ambient (a greenhouse pulls ambient toward its target temp — see
    // Plant.Grow). Null bound = no limit on that side.
    public bool IsTempComfortableAtTemp(float t) {
        if (tempMin.HasValue && t < tempMin.Value) return false;
        if (tempMax.HasValue && t > tempMax.Value) return false;
        return true;
    }

    // Soil-moisture comfort — true when the soil tile's wetness is within range.
    // A SOFT gate in Plant.Grow: out of range only slows growth (DroughtGrowthRate),
    // it doesn't freeze it. `soilTile` is the solid tile directly below the plant
    // (where moisture physically lives) — null (world bottom edge or lookup miss) is
    // treated as "no data" → comfortable. Null bound = no limit.
    public bool IsMoistureComfortableAt(Tile soilTile) {
        if (soilTile == null) return true;
        return IsMoistureComfortableForLevel(soilTile.moisture);
    }

    // Comfort check against an explicit moisture level (0..100), used when the plant draws from a
    // self-contained greenhouse pool rather than a soil tile (see Plant's reservoir helpers). A
    // negative level means "no reservoir" → treated as comfortable so a misplaced plant doesn't
    // wither. Null bound = no limit on that side.
    public bool IsMoistureComfortableForLevel(int m) {
        if (m < 0) return true;
        if (moistureMin.HasValue && m < moistureMin.Value) return false;
        if (moistureMax.HasValue && m > moistureMax.Value) return false;
        return true;
    }

    // Season comfort — true when the current season is in the authored list.
    // A HARD gate in Plant.Grow: out of season freezes growth. Null/empty list =
    // no restriction (year-round). WeatherSystem null during early startup / tests
    // is treated as in-season so plants don't stall before the first tick.
    public bool IsSeasonComfortableAt(WeatherSystem weather) {
        if (seasons == null || seasons.Length == 0) return true;
        if (weather == null) return true;
        return Array.IndexOf(seasons, weather.GetSeason()) >= 0;
    }

    [OnDeserialized]
    new internal void OnDeserialized(StreamingContext context){
        costs = ncosts.Select(iq => new ItemQuantity(iq)).ToArray();
        products = nproducts.Select(iq => new ItemQuantity(iq)).ToArray();
        // Removal yields default to the harvest products when not authored separately.
        removalProducts = nremovalProducts != null
            ? nremovalProducts.Select(iq => new ItemQuantity(iq)).ToArray()
            : products;
        if (njob != null){
            job = Db.jobByName[njob];
        }
        // handle null or 0 growthTime?
    }

}