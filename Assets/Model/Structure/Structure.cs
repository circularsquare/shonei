using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Runtime.Serialization;


public class Structure {
    // ── Maintenance constants ──────────────────────────────────────────
    // Condition is a 0-1 float tracked on every non-plant structure with construction costs.
    // Below BreakThreshold the structure is non-functional (craft halts, road bonus lost,
    // light sources go dark, etc.); below RegisterThreshold a WOM Maintenance order becomes
    // active so menders will come repair it. A single visit restores up to MaxRepairPerTask.
    public const float BreakThreshold     = 0.5f;   // below → IsBroken, functionality gated off
    public const float RegisterThreshold  = 0.75f;  // below → WOM order active, mender may come
    public const float MaxRepairPerTask   = 0.40f;  // cap on condition restored in one mender visit
    public const float RepairWorkPerTick  = 0.05f;  // base condition gained per tick while working
    public const float RepairCostFraction = 0.25f;  // full 0→1 repair = ¼ of construction cost
    public const int   DaysToBreak        = 30;     // in-game days from 1.0 → BreakThreshold (0.5)

    public GameObject go;
    public int x;
    public int y;
    public StructType structType;
    public Sprite sprite;
    public SpriteRenderer sr;

    // ── Maintenance state ──────────────────────────────────────────────
    // 0.0 = fully broken, 1.0 = pristine. Decays in MaintenanceSystem.Tick(); restored by menders.
    // Persisted via StructureSaveData.condition. Default 1.0 also covers old saves.
    public float condition = 1.0f;

    // Opt-in gate: plants, nav-only structures (platform/stairs/ladder, via noMaintenance JSON flag),
    // and zero-cost structures are excluded from the maintenance system entirely.
    public bool NeedsMaintenance =>
        !structType.noMaintenance
        && !structType.isPlant
        && structType.ncosts != null
        && structType.ncosts.Length > 0;

    // Non-functional when broken. Gates craft/research/supply orders, road bonuses, light emission,
    // decoration happiness, leisure seat availability, and house sleep assignment.
    public bool IsBroken => NeedsMaintenance && condition < BreakThreshold;

    // WOM Maintenance order's isActive lambda reads this — order goes idle once repaired past 75%.
    public bool WantsMaintenance => NeedsMaintenance && condition < RegisterThreshold;

    // Used by Navigation.cs and ModifierSystem.cs for road speed bonus — returns 0 when broken
    // so neglected roads stop giving a path-cost discount / movement bonus.
    public float EffectivePathCostReduction => IsBroken ? 0f : structType.pathCostReduction;

    // The project-default material this SpriteRenderer was created with (URP 2D's
    // Sprite-Lit-Default, which carries the `LightMode = Universal2D` tag needed for
    // the NormalsCapture render-layer filter). Captured once in RefreshTint before
    // we ever swap to the cracked material, so we can restore it verbatim on repair
    // without hardcoding a shader name. Restoring via `Shader.Find("Sprites/Default")`
    // would silently substitute the *legacy* sprite shader and drop the structure out
    // of the lighting pipeline (no ambient, no sun). See SPEC-rendering.md §Lighting.
    Material defaultMat;

    // Re-applies the sprite material based on broken state. Called from
    // MaintenanceSystem on threshold crossings. Broken structures swap to the shared
    // CrackedSprite material which composites a tileable crack texture on top of the
    // base sprite, alpha-masked by the sprite so cracks only appear on visible pixels.
    // That shader is URP 2D-tagged so the NormalsCapture pipeline still picks up the
    // renderer (preserving ambient/sun/torch lighting on broken buildings).
    // Deconstruct blueprints override tint via sr.color in Blueprint.cs — independent
    // of the material swap, so broken + deconstructing composites correctly.
    public virtual void RefreshTint() {
        if (sr == null) return;
        if (defaultMat == null) defaultMat = sr.sharedMaterial;
        sr.sharedMaterial = IsBroken ? (GetCrackedMaterial() ?? defaultMat) : defaultMat;
    }

    // Loaded once per process: the material that drives the cracked look. See
    // Assets/Resources/Materials/CrackedSprite.mat. Null if the asset is missing —
    // callers fall back to the captured default material rather than crashing.
    static Material _crackedMaterialCache;
    static bool     _crackedMaterialProbed;
    static Material GetCrackedMaterial() {
        if (_crackedMaterialProbed) return _crackedMaterialCache;
        _crackedMaterialProbed = true;
        _crackedMaterialCache = Resources.Load<Material>("Materials/CrackedSprite");
        if (_crackedMaterialCache == null)
            Debug.LogWarning("CrackedSprite material not found at Resources/Materials/CrackedSprite — broken structures will render untinted.");
        return _crackedMaterialCache;
    }
    public Tile tile => World.instance.GetTileAt(x, y);
    public Tile workTile => World.instance.GetTileAt(
        x + (mirrored ? (structType.nx - 1 - structType.workTileX) : structType.workTileX),
        y + structType.workTileY);
    public Reservable res;
    // Per-seat reservables for leisure buildings. Each work tile gets its own Reservable(1)
    // so two mice won't path to the same seat. Null for non-leisure buildings.
    public Reservable[] seatRes;

    // Returns true if any seat reservable is available. Only meaningful for leisure buildings.
    public bool AnySeatAvailable() {
        if (seatRes == null) return false;
        for (int i = 0; i < seatRes.Length; i++)
            if (seatRes[i].Available()) return true;
        return false;
    }

    // Returns the tile for a specific work tile index (from structType.nworkTiles), with mirroring applied.
    public Tile WorkTileAt(int index) {
        var wt = structType.nworkTiles[index];
        return World.instance.GetTileAt(
            x + (mirrored ? (structType.nx - 1 - wt.dx) : wt.dx),
            y + wt.dy);
    }

    // Fire art child GO — a separate SpriteRenderer showing the fire/flame portion of
    // the building sprite. Loaded from `{name}_f.png` companion. LightSource toggles
    // visibility based on isLit so fire disappears when the light is off.
    // Null for buildings without a fire art companion.
    public GameObject fireGO;
    public SpriteRenderer fireSR;

    // Local pixel offsets (bottom-left origin, unmirrored) of the structure's water zone —
    // the opaque pixels of its `{name}_w.png` companion mask. Null when no companion exists.
    // Registered with WaterController by StructController.Place().
    public List<Vector2Int> waterPixelOffsets { get; private set; }

    // "World conditions allow this structure to be worked on" gate for Structure subclasses.
    // Returns false to suppress the WOM craft order without removing it. Combined with
    // `Building.disabled` at the call site as `!disabled && ConditionsMet()`.
    // Override in subclasses for runtime conditions (e.g. PumpBuilding requires water below).
    // Blueprint mirrors this method by convention (it's a sibling class, not a Structure subclass).
    public virtual bool ConditionsMet() => true;

    // Called by StructController after Place(). Override to register WOM orders or other post-placement setup.
    // Not called during load or world generation — Reconcile() handles order registration on both paths.
    public virtual void OnPlaced() { }

    // Whether this structure is horizontally mirrored (flipped left-right).
    // Affects sprite flipX, workTile/storageTile offsets, tileRequirement offsets, and stair pathfinding.
    public bool mirrored = false;

    public Structure(StructType st, int x, int y, bool mirrored = false){
        this.structType = st;
        this.x = x;
        this.y = y;
        this.mirrored = mirrored;

        go = new GameObject();
        float visualX = st.nx > 1 ? x + (st.nx - 1) / 2.0f : x;
        go.transform.position = st.depth == 3
            ? new Vector3(visualX, y - 1f/8f, 0)
            : new Vector3(visualX, y, 0);
        go.transform.SetParent(StructController.instance.transform, true);
        go.name = "structure_" + structType.name;

        Sprite loadedSprite = structType.LoadSprite();
        sprite = loadedSprite ?? Resources.Load<Sprite>("Sprites/Buildings/default");
        sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.flipX = mirrored;
        if (loadedSprite == null) {
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = new Vector2(structType.nx, Mathf.Max(1, structType.ny));
        }
        // Workstations don't use Structure.res — their WOM Craft order owns the reservation.
        // Leisure buildings use per-seat seatRes[] instead of a single res.
        if (structType.isLeisure && structType.capacity > 0) {
            res = null;
            seatRes = new Reservable[structType.nworkTiles.Length];
            for (int i = 0; i < seatRes.Length; i++)
                seatRes[i] = new Reservable(1);
        } else {
            res = (structType.capacity > 0 && !structType.isWorkstation) ? new Reservable(structType.capacity) : null;
        }

        if (structType.name == "clock") {
            var ch = go.AddComponent<ClockHand>();
            ch.structure = this;
        }

        // Register on tiles at the appropriate depth layer.
        int depth = st.depth;
        for (int i = 0; i < st.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t.structs[depth] != null)
                Debug.LogError($"Already a depth-{depth} structure at {x+i},{y}!");
            t.structs[depth] = this;
        }
        // Sort order by depth: 0=building(10), 1=platform(11), 2=foreground(40), 3=road(1).
        // StructType.sortingOrder overrides this when >= 0 (e.g. light-source buildings at 64).
        if (st.sortingOrder >= 0) sr.sortingOrder = st.sortingOrder;
        else if (depth == 0) sr.sortingOrder = 10;
        else if (depth == 1) sr.sortingOrder = 11;
        else if (depth == 2) sr.sortingOrder = 40;
        else if (depth == 3) sr.sortingOrder = 1;
        LightReceiverUtil.SetSortBucket(sr);

        // Fire art companion — toggleable child GO for flame/fire visuals.
        // LightSource.Update toggles this based on isLit + emission intensity.
        Sprite fireSprite = Resources.Load<Sprite>("Sprites/Buildings/" + st.name.Replace(" ", "") + "_f");
        if (fireSprite != null) {
            fireGO = new GameObject("fire");
            fireGO.transform.SetParent(go.transform, false);
            fireSR = fireGO.AddComponent<SpriteRenderer>();
            fireSR.sprite = fireSprite;
            fireSR.sortingOrder = sr.sortingOrder;
            fireSR.flipX = mirrored;
            LightReceiverUtil.SetSortBucket(fireSR);
            // Bind fire texture as _EmissionMap via MPB — secondary textures from
            // the sprite importer may not survive DrawRenderers with an override
            // material (EmissionWriter). Explicit MPB binding ensures it's always
            // available as a per-renderer property.
            var mpb = new MaterialPropertyBlock();
            fireSR.GetPropertyBlock(mpb);
            mpb.SetTexture(Shader.PropertyToID("_EmissionMap"), fireSprite.texture);
            fireSR.SetPropertyBlock(mpb);
            fireGO.SetActive(false);
        }

        // Scan sprite for water-marker pixels. Registration happens in StructController.Place().
        waterPixelOffsets = WaterController.ScanWaterPixels(sprite);
    }

    // Shared factory: dispatches to the correct subclass based on StructType properties.
    // Used by both StructController.Construct (gameplay) and SaveSystem.RestoreStructure (load).
    // When adding a new Structure subclass, add its case here — no other dispatch site needed.
    public static Structure Create(StructType st, int x, int y, bool mirrored = false) {
        if (st.isPlant)
            return new Plant(st as PlantType, x, y);
        if (st.depth == 0 || st.isBuilding) {
            if (st.name == "pump")   return new PumpBuilding(st, x, y, mirrored);
            if (st.name == "market") return new MarketBuilding(st, x, y, mirrored);
            if (st.name == "quarry") return new Quarry(st, x, y, mirrored);
            return new Building(st, x, y, mirrored);
        }
        return new Structure(st, x, y, mirrored); // platforms, ladders, stairs, foreground, roads
    }

    public virtual void Destroy(){
        if (waterPixelOffsets != null)
            WaterController.instance?.UnregisterDecorativeWater(this);
        WorkOrderManager.instance?.RemoveMaintenanceOrders(this);
        MaintenanceSystem.instance?.ForgetStructure(this);
        StructController.instance.Remove(this);
        int depth = structType.depth;
        for (int i = 0; i < structType.nx; i++) {
            Tile t = World.instance.GetTileAt(x + i, y);
            if (t == null) continue;
            t.structs[depth] = null;
        }
        GameObject.Destroy(go);
        World world = World.instance;
        int tileCount = structType.nx > 1 ? structType.nx : 1;
        for (int i = 0; i < tileCount; i++) {
            world.graph.UpdateNeighbors(x + i, y);
            world.graph.UpdateNeighbors(x + i, y + 1);
            world.FallIfUnstandable(x + i, y + 1);
        }
    }

}
