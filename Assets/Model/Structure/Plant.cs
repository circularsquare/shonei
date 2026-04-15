using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using System.Runtime.Serialization;

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
        age += t;
        // hardcoded 4 growth stages
        growthStage = Math.Min(age * 3 / plantType.growthTime, 3);
        if (growthStage >= 3 && !harvestable){
            harvestable = true;
        }
        UpdateSprite();
    }
    public void Mature(){
        Grow(plantType.growthTime);
    }
    public ItemQuantity[] Harvest(){
        if (!harvestable) { Debug.LogError($"Harvest() called on {plantType.name} but harvestable=false"); return Array.Empty<ItemQuantity>(); }
        harvestable = false;
        age = 0; // autoreplant
        growthStage = 0;
        return plantType.products;
    }

    public override void Destroy() {
        PlantController.instance.Remove(this);
        base.Destroy();
    }

    public void UpdateSprite(){
        string n = plantType.name.Replace(" ", "");
        sprite = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/g" + growthStage.ToString());
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Plants/Split/" + n + "/g0");}
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Plants/default");}
        sr.sprite = sprite;
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
    // public string njob {get; set;}
    // public Job job;

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