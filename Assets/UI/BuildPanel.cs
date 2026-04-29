using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class BuildPanel : MonoBehaviour {
    [SerializeField] GameObject buildDisplayPrefab;
    [SerializeField] GameObject textDisplayPrefab;
    [SerializeField] Transform subpanel;
    [SerializeField] Button btnStructures;
    [SerializeField] Button btnPlants;
    [SerializeField] Button btnProduction;
    [SerializeField] Button btnPower;
    [SerializeField] Button btnStorage;
    [SerializeField] Button btnTiles;
    public static BuildPanel instance { get; protected set; }
    public StructType structType;
    // Whether the next placed blueprint will be horizontally mirrored.
    // Toggled by the F key during Build mode. Resets when a new building type is selected.
    public bool mirrored = false;
    // 90° clockwise steps (0..3) the next placed blueprint will be rotated by.
    // Cycled by the R key during Build mode, gated on structType.rotatable.
    // Resets when a new building type is selected.
    public int rotation = 0;
    // Shape variant index for the next placed blueprint (e.g. height for tall platforms).
    // Cycled by Q (-1) and E (+1) during Build mode, gated on structType.HasShapes.
    // Resets when a new building type is selected. See StructType.shapes / Shape.
    public int shapeIndex = 0;

    static readonly string[] CategoryNames = { "structures", "plants", "production", "power", "storage", "tiles" };

    readonly Dictionary<string, GameObject> subPanels = new Dictionary<string, GameObject>();
    readonly Dictionary<string, Button> catButtons = new Dictionary<string, Button>();
    string openCategory = null;

    void Start() {
        if (instance != null) { Debug.LogError("there should only be one build panel: " + gameObject.name); return; }
        instance = this;
        if (subpanel == null) { Debug.LogError("BuildPanel: subpanel not assigned"); return; }

        // sort all struct types into categories
        var cats = new Dictionary<string, List<StructType>>();
        foreach (string c in CategoryNames) cats[c] = new List<StructType>();

        foreach (StructType st in Db.structTypes) {
            if (st == null) continue;
            if (st.defaultLocked) continue;
            string cat = st.isPlant ? "plants" : st.category;
            if (cat != null && cats.ContainsKey(cat)) cats[cat].Add(st);
        }

        // build one hidden sub-panel per category
        foreach (string cat in CategoryNames) {
            GameObject sp = new GameObject("SubPanel_" + cat);
            sp.transform.SetParent(subpanel, false);

            RectTransform rt = sp.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0f, 0f);

            VerticalLayoutGroup vlg = sp.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            vlg.childControlHeight = false;
            vlg.spacing = 2f;
            var csf = sp.AddComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

            foreach (StructType st in cats[cat]) {
                AddBuildDisplay(sp.transform, st);
            }

            sp.SetActive(false);
            subPanels[cat] = sp;
        }

        // hook up the manually-placed category buttons. Tooltips are attached here
        // (rather than in the editor) so the category-name text stays driven by
        // CategoryNames — no risk of editor labels drifting from code.
        catButtons["structures"] = btnStructures;
        catButtons["plants"]     = btnPlants;
        catButtons["production"] = btnProduction;
        catButtons["power"]      = btnPower;
        catButtons["storage"]    = btnStorage;
        catButtons["tiles"]      = btnTiles;
        foreach (var kv in catButtons) {
            Button btn = kv.Value;
            if (btn == null) continue;
            string cat = kv.Key;
            btn.onClick.AddListener(() => ToggleCategory(cat));
            Tooltippable tip = btn.GetComponent<Tooltippable>() ?? btn.gameObject.AddComponent<Tooltippable>();
            tip.title = cat;
            tip.body  = "";
        }
    }

    void AddBuildDisplay(Transform parent, StructType st) {
        GameObject go = Instantiate(buildDisplayPrefab, parent);
        Transform costsContainer = go.transform.Find("CostsContainer");
        foreach (ItemQuantity iq in st.costs) {
            GameObject costDisplay = Instantiate(textDisplayPrefab, costsContainer);
            costDisplay.GetComponent<TextMeshProUGUI>().text = iq.item.name + ": " + ItemStack.FormatQ(iq);
            costDisplay.name = "CostDisplay_" + iq.item.name;
        }
        StructType captured = st;
        go.transform.GetChild(0).GetComponent<Button>().onClick.AddListener(() => {
            SetStructType(captured);
            CloseSubPanel();
        });
        string buildingName = (st.name == "empty") ? "mine tile" : st.name;
        go.transform.Find("BuildingButton/TextBuildingName").GetComponent<TextMeshProUGUI>().text = buildingName;
        go.name = "BuildDisplay_" + st.name;
    }

    public void ToggleCategory(string cat) {
        bool wasOpen = openCategory == cat;
        CloseSubPanel();
        if (!wasOpen) {
            GameObject sp = subPanels[cat];
            sp.SetActive(true);
            openCategory = cat;
            // position sub-panel above its category button
            if (catButtons.TryGetValue(cat, out Button btn) && btn != null) {
                RectTransform btnRt = btn.GetComponent<RectTransform>();
                RectTransform spRt  = sp.GetComponent<RectTransform>();
                RectTransform subpanelRt = (RectTransform)subpanel;
                Vector3 btnLeftWorld = btnRt.TransformPoint(new Vector3(btnRt.rect.xMin, 0, 0));
                Vector2 localPos = subpanelRt.InverseTransformPoint(btnLeftWorld);
                spRt.anchoredPosition = new Vector2(localPos.x, spRt.anchoredPosition.y);
            }
        }
    }

    public void CloseSubPanel() {
        if (openCategory != null) {
            subPanels[openCategory].SetActive(false);
            openCategory = null;
        }
    }

    public void UnlockBuilding(string buildingName) {
        if (!Db.structTypeByName.TryGetValue(buildingName, out StructType st)) {
            Debug.LogWarning("UnlockBuilding: unknown building " + buildingName); return;
        }
        string cat = st.isPlant ? "plants" : st.category;
        if (cat == null || !subPanels.ContainsKey(cat)) return;
        AddBuildDisplay(subPanels[cat].transform, st);
    }

    public void LockBuilding(string buildingName) {
        if (!Db.structTypeByName.TryGetValue(buildingName, out StructType st)) {
            Debug.LogWarning("LockBuilding: unknown building " + buildingName); return;
        }
        string cat = st.isPlant ? "plants" : st.category;
        if (cat == null || !subPanels.ContainsKey(cat)) return;
        Transform panel = subPanels[cat].transform;
        Transform entry = panel.Find("BuildDisplay_" + buildingName);
        if (entry != null) Destroy(entry.gameObject);
    }

    public void SetStructType(StructType st) {
        structType = st;
        mirrored = false;
        rotation = 0;
        shapeIndex = 0;
        MouseController.instance.SetModeBuild();
    }

    public void ToggleMirror() { mirrored = !mirrored; }

    // Cycle 0→1→2→3→0. No-op if the current StructType isn't rotatable, so the key handler
    // can call this unconditionally without redundant gating.
    public void ToggleRotate() {
        if (structType == null || !structType.rotatable) return;
        rotation = (rotation + 1) % 4;
    }

    // Cycle through shape variants by `delta` (+1 = E, -1 = Q). Clamped within
    // [0, structType.shapes.Length-1]; no wrap so the player can hold E without surprise
    // shrinkage. No-op when the current StructType doesn't declare shapes.
    public void CycleShape(int delta) {
        if (structType == null || !structType.HasShapes) return;
        int n = structType.shapes.Length;
        shapeIndex = Mathf.Clamp(shapeIndex + delta, 0, n - 1);
    }

    public bool CanPlaceHere(StructType st, Tile tile) {
        return StructPlacement.CanPlaceHere(st, tile, mirrored, shapeIndex);
    }

    public bool PlaceBlueprint(Tile tile) {
        if (structType == null) return false;
        if (!CanPlaceHere(structType, tile)) return false;

        Blueprint blueprint = new Blueprint(structType, tile.x, tile.y, mirrored, rotation: rotation, shapeIndex: shapeIndex);
        SoundManager.instance?.PlaySFX("click");
        return true;
    }

    // Unified cancel/deconstruct entry point.
    //  - Regular blueprint on tile → refund delivered materials to floor, destroy bp.
    //  - Deconstruct blueprint on tile → cancel the pending deconstruction (no refund —
    //    deconstruct bps have no delivered materials; Destroy() also unlocks storage).
    //  - No bp but a structure present → queue a new deconstruct blueprint.
    //  - Empty tile → no-op, return false.
    public bool Remove(Tile tile) {
        Blueprint existingBp = tile.GetAnyBlueprint();
        if (existingBp != null) {
            if (existingBp.state != Blueprint.BlueprintState.Deconstructing) {
                foreach (var cost in existingBp.costs)
                    existingBp.inv.MoveItemTo(tile.EnsureFloorInventory(), cost.item, existingBp.inv.Quantity(cost.item));
            }
            existingBp.Destroy(); // sets cancelled, removes from bp list, WOM cleanup, unlocks storage if decon
            return true;
        }
        if (System.Array.Exists(tile.structs, s => s != null)) {
            Blueprint.CreateDeconstructBlueprint(tile);
            return true;
        }
        return false;
    }
}
