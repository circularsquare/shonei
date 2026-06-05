using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class BuildPanel : MonoBehaviour {
    [SerializeField] GameObject buildDisplayPrefab;
    [SerializeField] GameObject textDisplayPrefab;
    [SerializeField] Transform subpanel;
    // Data-driven category tabs. categoryButtonPrefab is cloned once per BuildCat into
    // categoryBar; categoryInsertIndex is the sibling slot the first category button lands
    // at — the bar also holds non-category tool buttons (select before, remove/harvest after).
    [SerializeField] GameObject categoryButtonPrefab;
    [SerializeField] Transform categoryBar;
    [SerializeField] int categoryInsertIndex = 1;
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
    // Ctrl+Shift+F in MouseController; auto-disarms after one successful place.
    public static bool instantBuildNext = false;

    // Build-menu tabs, in display order. To add a tab: add an entry here, author the
    // buildings' "category" in buildingsDb.json, and drop a <icon>.png in
    // Sprites/Misc/buildicons/. No scene wiring — buttons spawn from categoryButtonPrefab.
    //   key   — matches StructType.category (st.isPlant routes to "plants")
    //   label — tooltip title
    //   icon  — sprite stem under Sprites/Misc/buildicons/ (filenames don't all match keys)
    struct BuildCat {
        public string key, label, icon;
        public BuildCat(string key, string label, string icon) { this.key = key; this.label = label; this.icon = icon; }
    }
    static readonly BuildCat[] Categories = {
        new BuildCat("tiles",      "tiles",      "tiles"),
        new BuildCat("structures", "structures", "structures"),
        new BuildCat("plants",     "plants",     "plant"),
        new BuildCat("production", "production", "production"),
        new BuildCat("power",      "power",      "power"),
        new BuildCat("storage",    "storage",    "storage"),
        new BuildCat("housing",    "housing",    "housing"),
    };

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
        if (categoryButtonPrefab == null || categoryBar == null) { Debug.LogError("BuildPanel: categoryButtonPrefab/categoryBar not assigned"); return; }

        // sort all struct types into categories
        var cats = new Dictionary<string, List<StructType>>();
        foreach (BuildCat c in Categories) cats[c.key] = new List<StructType>();

        foreach (StructType st in Db.structTypes) {
            if (st == null) continue;
            if (st.defaultLocked) continue;
            // Plants only appear once their seed has been discovered — otherwise the player
            // sees plants they can't grow (e.g. a tree entry with no acorns). Undiscovered
            // ones are added later by RefreshPlantVisibility when the seed turns up.
            if (!PlantSeedDiscovered(st)) continue;
            string cat = st.isPlant ? "plants" : st.category;
            if (cat != null && cats.ContainsKey(cat)) cats[cat].Add(st);
        }

        // build one hidden sub-panel per category
        foreach (BuildCat bc in Categories) {
            string cat = bc.key;
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

        // Spawn one category button per BuildCat from the prefab, slotting them into
        // categoryBar after the leading tool button(s) so the bar reads select | tabs |
        // remove | harvest. Icon, onClick, and tooltip are all code-driven, so adding a
        // tab never needs scene work.
        for (int i = 0; i < Categories.Length; i++) {
            BuildCat bc = Categories[i];
            GameObject btnGo = Instantiate(categoryButtonPrefab, categoryBar);
            btnGo.name = "BuildCat_" + bc.key;
            btnGo.transform.SetSiblingIndex(categoryInsertIndex + i);

            // Icon lives on the button's "Icon" child (the button's own Image is the woodframe).
            Transform iconT = btnGo.transform.Find("Icon");
            Sprite iconSprite = Resources.Load<Sprite>("Sprites/Misc/buildicons/" + bc.icon);
            if (iconT != null && iconSprite != null) iconT.GetComponent<Image>().sprite = iconSprite;
            else Debug.LogWarning("BuildPanel: missing Icon child or sprite for category '" + bc.key + "'");

            Button btn = btnGo.GetComponent<Button>();
            string cat = bc.key;
            btn.onClick.AddListener(() => ToggleCategory(cat));
            Tooltippable tip = btn.GetComponent<Tooltippable>() ?? btn.gameObject.AddComponent<Tooltippable>();
            tip.title = bc.label;
            tip.body  = "";
            catButtons[cat] = btn;
            // Hide categories with no unlocked buildings — clicking them otherwise opens
            // an empty sub-panel. Re-shown by UnlockBuilding when research fills the category.
            btnGo.SetActive(cats[cat].Count > 0);
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
        string buildingName = (st.name == "empty") ? "mine tile" : st.DisplayName;
        go.transform.Find("BuildingButton/TextBuildingName").GetComponent<TextMeshProUGUI>().text = buildingName;
        go.name = "BuildDisplay_" + st.name;

        // Hover tooltip from the StructType's description (e.g. burrow "houses 2"). Attached
        // to the BuildingButton (it has the raycast-target Button, so pointer events fire) and
        // driven by JSON data rather than per-prefab editor wiring.
        if (!string.IsNullOrEmpty(st.description)) {
            Transform btn = go.transform.Find("BuildingButton") ?? go.transform;
            Tooltippable tip = btn.GetComponent<Tooltippable>() ?? btn.gameObject.AddComponent<Tooltippable>();
            tip.title = buildingName;
            tip.body  = st.description;
        }
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

    // True when st can be shown in the build menu as far as input discovery is concerned:
    // non-plants are unconstrained; a plant requires every input item (its seed) discovered.
    bool PlantSeedDiscovered(StructType st) {
        if (!st.isPlant || st.costs == null) return true;
        foreach (ItemQuantity iq in st.costs)
            if (iq.item != null && !iq.item.IsDiscovered()) return false;
        return true;
    }

    // Reconciles plant entries against current seed-discovery state. Idempotent: adds a plant's
    // entry when its seed becomes discovered (picked up, traded for, researched) and removes it
    // when the seed is no longer discovered (after a save load / world reset). Called by
    // InventoryController whenever discovery state changes or is re-seeded.
    //
    // Scoped to the seed gate only — research-locked plants are owned by Unlock/LockBuilding, so
    // defaultLocked plants are left untouched here. (No plant is currently both seed- and research-
    // gated; if one were added, the two gates would need to be unified.)
    public void RefreshPlantVisibility() {
        if (!subPanels.TryGetValue("plants", out GameObject sp) || sp == null) return;
        Transform panel = sp.transform;
        foreach (StructType st in Db.structTypes) {
            if (st == null || !st.isPlant || st.defaultLocked) continue;
            bool shouldShow = PlantSeedDiscovered(st);
            Transform entry = panel.Find("BuildDisplay_" + st.name);
            if (shouldShow && entry == null) AddBuildDisplay(panel, st);
            else if (!shouldShow && entry != null) Destroy(entry.gameObject);
        }
        RefreshPlantCategoryButton();
    }

    // Shows the plants category button only when at least one plant is buildable. Counts from the
    // data model rather than panel.childCount because Destroy is deferred — just-removed entries
    // would still be counted this frame.
    void RefreshPlantCategoryButton() {
        if (!catButtons.TryGetValue("plants", out Button btn) || btn == null) return;
        int n = 0;
        foreach (StructType st in Db.structTypes)
            if (st != null && st.isPlant && !st.defaultLocked && PlantSeedDiscovered(st)) n++;
        btn.gameObject.SetActive(n > 0);
        if (n == 0 && openCategory == "plants") CloseSubPanel();
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

    // Cursor-position-driven ladder variant resolution. When the player is in ladder build
    // mode, hovering near the left/right edge of a tile switches the placement to the
    // sideladder StructType (mounted to the adjacent wall surface). The cost panel and
    // hotkeys keep reading the selected `structType` (ladder); only what gets *placed*
    // switches. Returns the resolved (StructType, mirrored, anchor) — the resolved
    // StructType stays "ladder" (centred) when the cursor is in the middle of a tile or
    // when ladder isn't selected at all.
    //
    // dir/mirror convention for sideladder: mirrored=true → wall on right of the ladder's
    // tile; mirrored=false → wall on left. See Tile.HasSideLadder.
    public static void ResolveLadderVariant(Vector2 world, Tile tileAt, StructType activeSt,
                                            out StructType resolvedSt, out bool resolvedMirrored,
                                            out Tile resolvedAnchor) {
        resolvedSt = activeSt;
        resolvedMirrored = BuildPanel.instance != null && BuildPanel.instance.mirrored;
        resolvedAnchor = tileAt;
        if (activeSt == null || activeSt.name != "ladder" || tileAt == null) return;
        StructType sideSt = Db.structTypeByName.TryGetValue("ladder_side", out var ss) ? ss : null;
        if (sideSt == null) return;

        // Tiles are center-pivot — tile (tx, _) spans world.x ∈ [tx-0.5, tx+0.5).
        // World.GetTileAt(float) uses floor(x + 0.5) to snap. Convert to [0,1] relative
        // to the SNAPPED tile, not raw floor(world.x) (which is off by half a tile and
        // makes the cursor-at-tile-center read as "left edge").
        float frac = world.x - tileAt.x + 0.5f;
        const float EDGE = 0.2f;
        bool hereSolid = tileAt.type.solid || tileAt.structs[0] != null;
        World w = World.instance;

        if (frac < EDGE) {
            // Cursor near left edge. Ladder mounts onto the wall to the left of its own tile.
            if (hereSolid) {
                Tile air = w.GetTileAt(tileAt.x - 1, tileAt.y);
                if (air == null) return;
                resolvedSt = sideSt; resolvedMirrored = true; resolvedAnchor = air; // wall (tileAt) on right of air
            } else {
                resolvedSt = sideSt; resolvedMirrored = false; resolvedAnchor = tileAt;
            }
        } else if (frac > 1f - EDGE) {
            if (hereSolid) {
                Tile air = w.GetTileAt(tileAt.x + 1, tileAt.y);
                if (air == null) return;
                resolvedSt = sideSt; resolvedMirrored = false; resolvedAnchor = air; // wall (tileAt) on left of air
            } else {
                resolvedSt = sideSt; resolvedMirrored = true; resolvedAnchor = tileAt;
            }
        }
    }

    public bool PlaceBlueprint(Tile tile, StructType placeSt = null, bool? placeMirrored = null) {
        if (structType == null) return false;
        // Overrides let the caller (MouseController) commit a cursor-resolved variant
        // (e.g. ladder → sideladder) without mutating BuildPanel.structType, so the cost
        // panel and selected-tool indicator keep showing the player's chosen build mode.
        StructType effSt = placeSt ?? structType;
        bool effMirrored = placeMirrored ?? mirrored;

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

        string why = StructPlacement.GetPlacementFailReason(effSt, tile, effMirrored, shapeIndex);
        if (why != null) {
            EventFeed.instance?.Post($"<color=#cc3333>{why}</color>", EventFeed.Category.Alert);
            return false;
        }
        Blueprint blueprint = new Blueprint(effSt, tile.x, tile.y, effMirrored, rotation: rotation, shapeIndex: shapeIndex);
        SoundManager.instance?.PlaySFX("click");
        // Debug one-shot: skip the worker/supply step entirely. Complete() with an empty
        // inventory consumes no resources (the foreach over inv.itemStacks is a no-op).
        // Bypasses the suspended-support gate by design — same spirit as Ctrl+Shift+D.
        if (instantBuildNext) {
            instantBuildNext = false;
            Debug.Log($"[debug] instant-build {effSt.name} at ({tile.x}, {tile.y})");
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
