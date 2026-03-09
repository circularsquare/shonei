using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class BuildPanel : MonoBehaviour {
    public GameObject buildDisplayPrefab;
    public GameObject textDisplayPrefab;
    public Transform subpanel;
    public Button btnStructures;
    public Button btnPlants;
    public Button btnProduction;
    public Button btnStorage;
    public static BuildPanel instance;
    public StructType structType;

    static readonly string[] CategoryNames = { "structures", "plants", "production", "storage" };

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
            var csf = sp.AddComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

            foreach (StructType st in cats[cat]) {
                AddBuildDisplay(sp.transform, st);
            }

            sp.SetActive(false);
            subPanels[cat] = sp;
        }

        // hook up the manually-placed category buttons
        catButtons["structures"] = btnStructures;
        catButtons["plants"]     = btnPlants;
        catButtons["production"] = btnProduction;
        catButtons["storage"]    = btnStorage;
        btnStructures?.onClick.AddListener(() => ToggleCategory("structures"));
        btnPlants?.onClick.AddListener(     () => ToggleCategory("plants"));
        btnProduction?.onClick.AddListener( () => ToggleCategory("production"));
        btnStorage?.onClick.AddListener(    () => ToggleCategory("storage"));
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

    public void SetStructType(StructType st) {
        structType = st;
        MouseController.instance.SetModeBuild();
    }

    public bool PlaceBlueprint(Tile tile) {
        if (structType == null) return false;
        if (tile.GetBlueprintAt(structType.depth) != null) return false;

        if (tile.type.id != 0 && structType.name != "empty" && structType.requiredTileName == null) return false;
        if (structType.requiredTileName != null && tile.type.name != structType.requiredTileName) return false;

        if (!structType.isTile) {
            if ((structType.isPlant || structType.depth == "b") && tile.building != null) return false;
            if (structType.depth == "m" && tile.mStruct != null) return false;
            if (structType.depth == "f" && tile.fStruct != null) return false;
        }

        Blueprint blueprint = new Blueprint(structType, tile.x, tile.y);
        return true;
    }

    public bool Remove(Tile tile) {
        Blueprint existingBp = tile.GetBlueprintAt("b") ?? tile.GetBlueprintAt("m") ?? tile.GetBlueprintAt("f");
        if (existingBp != null && existingBp.state != Blueprint.BlueprintState.Deconstructing) {
            foreach (var cost in existingBp.costs)
                existingBp.inv.MoveItemTo(tile.EnsureFloorInventory(), cost.item, existingBp.inv.Quantity(cost.item));
                // TODO: this can break
            existingBp.cancelled = true;
            tile.SetBlueprintAt(existingBp.structType.depth, null);
            GameObject.Destroy(existingBp.go);
            return true;
        }
        if (tile.building != null || tile.mStruct != null || tile.fStruct != null) {
            Blueprint.CreateDeconstructBlueprint(tile);
            return true;
        }
        return false;
    }
}
