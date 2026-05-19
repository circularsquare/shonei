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
    // Two-click placement state: when StructType.placementMethod == "twoClick", the
    // first click on a valid tile stashes it here; the second click commits a
    // single blueprint carrying BOTH endpoints. Cleared on structType change,
    // Esc, mode switch, and on commit. See StructPlacement.CanPlaceTwoPoint.
    public Tile firstEndpoint;
    public bool AwaitingSecondClick => firstEndpoint != null;
    // Debug one-shot: when true, the next blueprint placed via PlaceBlueprint is
    // instantly completed (no resources consumed, no worker step). Armed via
    // Ctrl+Alt+B in MouseController; auto-disarms after one successful place.
    public static bool instantBuildNext = false;

    static readonly string[] CategoryNames = { "structures", "plants", "production", "power", "storage", "tiles" };

    readonly Dictionary<string, GameObject> subPanels = new Dictionary<string, GameObject>();
    readonly Dictionary<string, Button> catButtons = new Dictionary<string, Button>();
    string openCategory = null;
    // Empty-string is treated as closed. Defends against stale UnityEvent listeners
    // (which default string args to "") and against scene-reload races where some
    // path sets openCategory before subPanels has its entries.
    public bool IsSubPanelOpen => !string.IsNullOrEmpty(openCategory);

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
            // Hide categories with no unlocked buildings — clicking them otherwise opens
            // an empty sub-panel. Re-shown by UnlockBuilding when research fills the category.
            btn.gameObject.SetActive(cats[cat].Count > 0);
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
        // TryGetValue rather than [] so an unknown openCategory (stale state) doesn't
        // throw — we always want this to reset cleanly. Log when it happens so we can
        // chase the root cause if it recurs.
        if (!string.IsNullOrEmpty(openCategory)) {
            if (subPanels.TryGetValue(openCategory, out GameObject sp)) {
                sp.SetActive(false);
            } else {
                Debug.LogWarning("[BuildPanel] CloseSubPanel: openCategory='" + openCategory
                    + "' not in subPanels — clearing stale state.");
            }
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
        // Category may have been hidden because it was empty — show it now.
        if (catButtons.TryGetValue(cat, out Button catBtn) && catBtn != null) {
            catBtn.gameObject.SetActive(true);
        }
    }

    public void LockBuilding(string buildingName) {
        if (!Db.structTypeByName.TryGetValue(buildingName, out StructType st)) {
            Debug.LogWarning("LockBuilding: unknown building " + buildingName); return;
        }
        string cat = st.isPlant ? "plants" : st.category;
        if (cat == null || !subPanels.ContainsKey(cat)) return;
        Transform panel = subPanels[cat].transform;
        Transform entry = panel.Find("BuildDisplay_" + buildingName);
        if (entry == null) return;
        // childCount==1 means we're about to remove the last entry; hide the category
        // button so an empty sub-panel can't be opened. (Destroy is deferred, so check
        // before calling it.) Also close the sub-panel if it was the one open.
        bool nowEmpty = panel.childCount == 1;
        Destroy(entry.gameObject);
        if (nowEmpty) {
            if (openCategory == cat) CloseSubPanel();
            if (catButtons.TryGetValue(cat, out Button catBtn) && catBtn != null) {
                catBtn.gameObject.SetActive(false);
            }
        }
    }

    public void SetStructType(StructType st) {
        structType = st;
        mirrored = false;
        rotation = 0;
        shapeIndex = 0;
        firstEndpoint = null;  // changing structType always cancels an in-flight two-click placement
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

        // Two-click placement (rope bridges): first click stashes the endpoint;
        // second click validates the span and creates ONE blueprint carrying
        // both posts' coords. Returns true on the SECOND click only — the caller
        // (MouseController) uses that to know when to exit Build mode.
        if (structType.placementMethod == "twoClick") {
            if (firstEndpoint == null) {
                // First click: standalone single-tile feasibility check (cheap).
                // Full two-point validation happens on the second click.
                string whyFirst = StructPlacement.GetPlacementFailReason(structType, tile, mirrored, shapeIndex);
                if (whyFirst != null) {
                    EventFeed.instance?.Post($"<color=#cc3333>{whyFirst}</color>", EventFeed.Category.Alert);
                    return false;
                }
                firstEndpoint = tile;
                SoundManager.instance?.PlaySFX("click");
                return false;  // don't exit Build mode yet
            }
            // Second click.
            Tile a = firstEndpoint;
            string whyTwo = StructPlacement.GetTwoPointFailReason(structType, a, tile);
            if (whyTwo != null) {
                // Invalid second point: clear firstEndpoint so the first-post
                // ghost goes away. The reason is surfaced as a toast via EventFeed.
                EventFeed.instance?.Post($"<color=#cc3333>{whyTwo}</color>", EventFeed.Category.Alert);
                firstEndpoint = null;
                return false;
            }
            firstEndpoint = null;
            // Mirror is geometry-driven for two-click bridges: the LEFT post
            // (smaller x) is mirrored so its pole faces right toward the bridge.
            // Pass the anchor's mirror into the Blueprint so its primary ghost
            // matches; the partner ghost flips it.
            bool anchorIsLeft = a.x <= tile.x;
            Blueprint bridgeBp = new Blueprint(structType, a.x, a.y, anchorIsLeft,
                rotation: rotation, shapeIndex: shapeIndex,
                x2: tile.x, y2: tile.y);
            SoundManager.instance?.PlaySFX("click");
            if (instantBuildNext) {
                instantBuildNext = false;
                Debug.Log($"[debug] instant-build {structType.name} at ({a.x},{a.y}) ↔ ({tile.x},{tile.y})");
                bridgeBp.Complete();
            }
            return true;
        }

        string why = StructPlacement.GetPlacementFailReason(structType, tile, mirrored, shapeIndex);
        if (why != null) {
            EventFeed.instance?.Post($"<color=#cc3333>{why}</color>", EventFeed.Category.Alert);
            return false;
        }
        Blueprint blueprint = new Blueprint(structType, tile.x, tile.y, mirrored, rotation: rotation, shapeIndex: shapeIndex);
        SoundManager.instance?.PlaySFX("click");
        // Debug one-shot: skip the worker/supply step entirely. Complete() with an empty
        // inventory consumes no resources (the foreach over inv.itemStacks is a no-op).
        // Bypasses the suspended-support gate by design — same spirit as Ctrl+Shift+D.
        if (instantBuildNext) {
            instantBuildNext = false;
            Debug.Log($"[debug] instant-build {structType.name} at ({tile.x}, {tile.y})");
            blueprint.Complete();
        }
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
                // Refund onto the bp's anchor, not the clicked cell — for multi-tile bps
                // those differ when the player right-clicks a non-anchor footprint tile.
                Tile refundTile = existingBp.tile;
                foreach (var cost in existingBp.costs)
                    existingBp.inv.MoveItemTo(refundTile.EnsureFloorInventory(), cost.item, existingBp.inv.Quantity(cost.item));
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
