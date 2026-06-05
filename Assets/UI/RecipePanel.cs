using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Full-screen recipe panel — scrollable list of recipe cards.
//
// Unity setup:
//   recipeListContent  — Transform (ScrollView's Content object)
//                        Add VerticalLayoutGroup (spacing=4, childForceExpandWidth=true)
//                        Add ContentSizeFitter   (Vertical Fit = Preferred Size)
//   recipeDisplayPrefab — RecipeDisplay prefab
//
//   RecipePanel root RectTransform: Anchor Min=(0,0) Anchor Max=(1,1) Left=Right=Top=Bottom=20
//   Set the GameObject inactive by default.
//
//   Wire a UI button: onClick → RecipePanel.instance.Toggle()

public class RecipePanel : MonoBehaviour {
    public static RecipePanel instance { get; protected set; }

    [Header("UI Refs")]
    public Transform      recipeListContent;
    public RecipeDisplay  recipeDisplayPrefab;
    public ScrollRect     scrollRect;
    public Button         closeButton; // optional X in the corner

    readonly List<RecipeGroupDisplay> spawnedGroups   = new List<RecipeGroupDisplay>();
    readonly HashSet<int>             disabledRecipes = new HashSet<int>();
    // Workstation tiles whose group is expanded. Instance field (like disabledRecipes)
    // so it dies with the GameObject — no domain-reload reset hook needed. Persisted;
    // Rebuild reads it but never clears it (see ClearExpandedGroups / SetExpandedGroups).
    readonly HashSet<string>          expandedGroups  = new HashSet<string>();

    // Processes (ProcessorRecipe) the player has turned off, keyed by building name (one
    // process per building today). Gates the FillProcessor work order so no new batches
    // start. Instance field like disabledRecipes; persisted via SaveSystem.
    readonly HashSet<string>          disabledProcesses = new HashSet<string>();

    // Real recipe ids the single "write a book" proxy stands in for (one per tech book).
    // Rebuilt each Rebuild(); used so the proxy's On/Off drives every book recipe at once.
    readonly List<int>                bookRecipeIds   = new List<int>();
    // Sentinel id for the book proxy card — never a real Db.recipes id (those are >= 0),
    // so it can't collide and is never written to disabledRecipes directly.
    public const int                  BookProxyRecipeId = -100;

    float       refreshTimer;
    const float RefreshInterval = 0.5f;

    // Master-detail: left = the grouped list (existing scroll), right = a detail pane
    // showing the selected recipe. Assign detailPane in the scene to tweak it in the
    // editor; if left null, BuildLayout builds the split in code as a fallback.
    [SerializeField] RectTransform detailPane;
    RectTransform detailContainer;
    RecipeDisplay detailCard;
    RecipeListRow selectedRow;
    bool          layoutBuilt;
    const float   LeftWidth = 250f; // left list column width; detail pane starts just past it

    void Awake() {
        if (instance != null) { Debug.LogError("two RecipePanels!"); }
        instance = this;
        UI.RegisterExclusive(gameObject);
        if (closeButton != null) closeButton.onClick.AddListener(() => gameObject.SetActive(false));
    }

    void OnEnable() {
        BuildLayout();
        Rebuild();
    }

    void Update() {
        refreshTimer += Time.deltaTime;
        if (refreshTimer >= RefreshInterval) {
            refreshTimer = 0f;
            foreach (var group in spawnedGroups) group.RefreshVisibleCards();
            if (detailCard != null) detailCard.Refresh(); // live have-amounts in the detail pane
        }
    }

    public void Toggle() {
        if (gameObject.activeSelf) gameObject.SetActive(false);
        else UI.OpenExclusive(gameObject);
    }

    // ── Master-detail layout + selection ───────────────────────────────

    // One-time split of the panel: the existing scroll becomes a fixed-width left column,
    // and a right detail pane is created in code (no manual scene wiring). Idempotent.
    void BuildLayout() {
        if (layoutBuilt) return;
        layoutBuilt = true;

        // Always narrow the scroll to a fixed-width left column (code; idempotent + robust,
        // so we never have to edit the scene's scroll RectTransform). The serialized
        // scrollRect ref isn't wired in the scene — derive it from the content hierarchy.
        if (scrollRect == null && recipeListContent != null)
            scrollRect = recipeListContent.GetComponentInParent<ScrollRect>();
        RectTransform root = null;
        if (scrollRect != null) {
            var scrollRT = scrollRect.GetComponent<RectTransform>();
            root = scrollRT.parent as RectTransform;
            scrollRT.anchorMin = new Vector2(0f, 0f);
            scrollRT.anchorMax = new Vector2(0f, 1f);
            scrollRT.offsetMin = new Vector2(2f, 2f);
            scrollRT.offsetMax = new Vector2(2f + LeftWidth, -2f);
        }

        // Detail container: an editor-authored DetailPane (wired in the scene, tweakable)
        // if present; otherwise build one in code so the panel still works unwired.
        if (detailPane != null) { detailContainer = detailPane; return; }
        if (root == null) { Debug.LogError("RecipePanel: no panel root for the detail pane"); return; }

        var dGO = new GameObject("DetailPane", typeof(RectTransform));
        dGO.transform.SetParent(root, false);
        detailContainer = dGO.GetComponent<RectTransform>();
        detailContainer.anchorMin = new Vector2(0f, 0f);
        detailContainer.anchorMax = new Vector2(1f, 1f);
        detailContainer.offsetMin = new Vector2(LeftWidth + 8f, 2f);
        detailContainer.offsetMax = new Vector2(-2f, -2f);
        detailContainer.SetAsFirstSibling(); // draw behind the close button + scroll
    }

    // Called by a list row's body click.
    public void Select(RecipeListRow row) {
        if (selectedRow != null && selectedRow != row) selectedRow.SetSelected(false);
        selectedRow = row;
        if (row != null) row.SetSelected(true);
        BuildDetail();
    }

    // Rebuilds the right pane for the current selection — fresh RecipeDisplay each time
    // (RecipeDisplay.Setup assumes a clean card, so we don't re-Setup one instance).
    void BuildDetail() {
        if (detailCard != null) { Destroy(detailCard.gameObject); detailCard = null; }
        if (selectedRow == null || detailContainer == null || recipeDisplayPrefab == null) return;

        detailCard = Instantiate(recipeDisplayPrefab, detailContainer, false);
        detailCard.name = "DetailCard";
        if (selectedRow.ProcessData != null) detailCard.Setup(selectedRow.ProcessData);
        else                                 detailCard.Setup(selectedRow.RecipeData);
        LayoutUtil.RebuildImmediate(detailContainer);
    }

    void ClearSelection() {
        selectedRow = null; // its row GameObject is being destroyed by Rebuild
        BuildDetail();      // tears down the detail card + shows the placeholder
    }

    void Rebuild() {
        if (recipeListContent == null) { Debug.LogError("RecipePanel: recipeListContent not assigned"); return; }
        if (recipeDisplayPrefab == null) { Debug.LogError("RecipePanel: recipeDisplayPrefab not assigned"); return; }

        // Clears spawned GameObjects only — NOT expandedGroups (it must survive Rebuild
        // so Phase-5 save restore is honoured on the next OnEnable).
        foreach (Transform child in recipeListContent) Destroy(child.gameObject);
        spawnedGroups.Clear();
        refreshTimer = 0f;
        ClearSelection(); // old rows are gone; reset detail to the placeholder

        // Group unlocked recipes by workstation (recipe.tile), preserving first-seen
        // order so the list order is the authored recipesDb order. Book recipes (one per
        // tech) collapse into a single "write a book" proxy so the scriptorium isn't a
        // wall of near-identical rows; bookRecipeIds tracks the real ones the proxy stands
        // in for, so its On/Off toggle can drive them all (see IsAllowed/SetAllowed).
        var order   = new List<string>();
        var byTile  = new Dictionary<string, List<Recipe>>();
        bookRecipeIds.Clear();
        var bookProxyTiles = new HashSet<string>();
        foreach (Recipe recipe in Db.recipes) {
            if (recipe == null) continue;
            if (recipe.hidden) continue; // dig/mine and other non-conventional pseudo-recipes
            if (ResearchSystem.instance != null && !ResearchSystem.instance.IsRecipeUnlocked(recipe.id)) continue;
            if (!IsWorkstationAvailable(recipe.tile)) continue; // hide workstations not yet researched/built
            if (!InputsDiscovered(recipe)) continue;            // hide recipes whose inputs the player has never had

            string tile = recipe.tile ?? "(none)";
            Recipe display = recipe;
            if (IsBookRecipe(recipe)) {
                bookRecipeIds.Add(recipe.id);
                if (!bookProxyTiles.Add(tile)) continue; // proxy already added for this tile
                display = BuildBookProxy(tile);
                if (display == null) continue; // missing paper/book item — skip rather than NRE
            }

            if (!byTile.TryGetValue(tile, out var list)) {
                list = new List<Recipe>();
                byTile[tile] = list;
                order.Add(tile);
            }
            list.Add(display);
        }

        // Fold in processes (passive timed conversions), grouped under their building —
        // so e.g. the brewery shows its craft recipe and its fermentation together. A
        // process-only building gets a group of its own, appended after the craft ones.
        var procByTile = new Dictionary<string, List<ProcessorRecipe>>();
        if (Db.processorRecipesByBuilding != null) {
            foreach (var kv in Db.processorRecipesByBuilding) {
                string tile = kv.Key;
                if (!IsWorkstationAvailable(tile)) continue; // hide processes until their building is reachable
                procByTile[tile] = kv.Value;
                if (!byTile.ContainsKey(tile)) { byTile[tile] = new List<Recipe>(); order.Add(tile); }
            }
        }

        for (int i = 0; i < order.Count; i++) {
            string tile = order[i];
            procByTile.TryGetValue(tile, out var procs);
            SpawnGroup(tile, byTile[tile], procs);
            if (i < order.Count - 1) SpawnDivider(); // separate workstation sections
        }

        if (scrollRect != null) StartCoroutine(ScrollToTop());
    }

    IEnumerator ScrollToTop() {
        yield return null; // wait for Destroy() to flush
        // Settle nested fitters bottom-up so restored-expanded groups don't pop.
        LayoutUtil.RebuildImmediate(recipeListContent as RectTransform);
        scrollRect.verticalNormalizedPosition = 1f;
    }

    static Sprite dividerSprite;
    void SpawnDivider() {
        if (dividerSprite == null) dividerSprite = Resources.Load<Sprite>("Sprites/Misc/divider");
        var go = new GameObject("Divider", typeof(RectTransform));
        go.transform.SetParent(recipeListContent, false);
        // recipeListContent's VerticalLayoutGroup has childControlHeight=0, so it won't
        // size the divider — set the rect explicitly (group width, 2px tall) like SpawnGroup.
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(LeftWidth, 2f);
        var img = go.AddComponent<Image>();
        img.sprite = dividerSprite;
        img.type   = Image.Type.Sliced;
        img.raycastTarget = false;
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 2f;
    }

    void SpawnGroup(string tile, List<Recipe> recipes, List<ProcessorRecipe> processes) {
        StructType st = (Db.structTypeByName != null && Db.structTypeByName.TryGetValue(tile, out var t)) ? t : null;

        var go = new GameObject("RecipeGroup_" + tile, typeof(RectTransform));
        go.transform.SetParent(recipeListContent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(LeftWidth, 0f);

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing                = 2f;
        vlg.childAlignment         = TextAnchor.UpperLeft;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        var group = go.AddComponent<RecipeGroupDisplay>();
        group.Setup(tile, st, recipes, processes, recipeDisplayPrefab, IsGroupExpanded(tile));
        spawnedGroups.Add(group);
    }

    // --- Discovery / availability filters ---

    // True if the recipe's workstation is reachable: research-unlocked OR currently placed
    // in the world. Hides e.g. crucible/weaver recipes before that building exists. Unknown
    // or null tiles (recipes not bound to a real building) are never hidden.
    bool IsWorkstationAvailable(string tile) {
        if (string.IsNullOrEmpty(tile)) return true;
        if (Db.structTypeByName == null || !Db.structTypeByName.TryGetValue(tile, out StructType st) || st == null) return true;
        if (StructController.instance != null) {
            List<Structure> placed = StructController.instance.GetByType(st);
            if (placed != null && placed.Count > 0) return true;
        }
        return ResearchSystem.instance == null || ResearchSystem.instance.IsBuildingUnlocked(st.name);
    }

    // True once every input item has been discovered (ever obtained). Hides variants for
    // raw materials the player has never had — e.g. the oak-planks recipe before any oak
    // shows up. discoveredItems walks the parent chain, so a group input ("wood") counts as
    // discovered once any leaf (pine) appears. Empty-input recipes (pump/dig) pass vacuously.
    bool InputsDiscovered(Recipe r) {
        InventoryController ic = InventoryController.instance;
        if (ic == null || ic.discoveredItems == null || r.inputs == null) return true;
        foreach (ItemQuantity iq in r.inputs) {
            if (iq?.item == null) continue;
            if (ic.discoveredItems.TryGetValue(iq.item.id, out bool discovered) && !discovered) return false;
        }
        return true;
    }

    // --- Book recipe collapse ---

    // True for the per-tech book recipes (and the authored fiction book): a single output
    // of ItemClass.Book. These collapse into one "write a book" card.
    static bool IsBookRecipe(Recipe r) =>
        r.outputs != null && r.outputs.Length > 0 && r.outputs[0].item != null
        && r.outputs[0].item.itemClass == ItemClass.Book;

    // Display-only stand-in for all of a tile's book recipes. NOT added to Db.recipes; its
    // On/Off routes to every real book recipe via the BookProxyRecipeId sentinel. Output is
    // the "book" group item, which carries the shared book sprite + generic "book" label.
    Recipe BuildBookProxy(string tile) {
        if (Db.itemByName == null) return null;
        if (!Db.itemByName.TryGetValue("paper", out Item paper)) return null;
        if (!Db.itemByName.TryGetValue("book",  out Item book))  return null;
        return new Recipe {
            id          = BookProxyRecipeId,
            job         = "scribe",
            tile        = tile,
            description = "write a book",
            inputs   = new[] { new ItemQuantity(paper, ItemStack.LiangToFen(1f)) },
            outputs  = new[] { new ItemQuantity(book,  ItemStack.LiangToFen(1f)) },
            ninputs  = new ItemNameQuantity[0],
            noutputs = new ItemNameQuantity[0],
        };
    }

    // --- Allow / disable ---

    public bool IsAllowed(int recipeId) {
        // The book proxy is "on" if any book recipe is still enabled (all-off => Off).
        if (recipeId == BookProxyRecipeId) {
            foreach (int id in bookRecipeIds) if (!disabledRecipes.Contains(id)) return true;
            return bookRecipeIds.Count == 0;
        }
        return !disabledRecipes.Contains(recipeId);
    }

    public void SetAllowed(int recipeId, bool allowed) {
        // Toggling the book proxy drives every real book recipe at once.
        if (recipeId == BookProxyRecipeId) {
            foreach (int id in bookRecipeIds) {
                if (allowed) disabledRecipes.Remove(id);
                else         disabledRecipes.Add(id);
            }
            return;
        }
        if (allowed) disabledRecipes.Remove(recipeId);
        else         disabledRecipes.Add(recipeId);
    }

    public int  DisabledCount                    => disabledRecipes.Count;
    public void CopyDisabledIds(int[] dest)      => disabledRecipes.CopyTo(dest);
    public void ClearDisabled()                  => disabledRecipes.Clear();

    // --- Process allow/disable (by building name; gates FillProcessor, see WorkOrderManager) ---

    public bool IsProcessAllowed(string building) => !disabledProcesses.Contains(building);

    public void SetProcessAllowed(string building, bool allowed) {
        if (allowed) disabledProcesses.Remove(building);
        else         disabledProcesses.Add(building);
    }

    public int      DisabledProcessCount             => disabledProcesses.Count;
    public string[] CopyDisabledProcesses()          { var a = new string[disabledProcesses.Count]; disabledProcesses.CopyTo(a); return a; }
    public void     SetDisabledProcesses(string[] b) { disabledProcesses.Clear(); if (b != null) foreach (string s in b) disabledProcesses.Add(s); }
    public void     ClearDisabledProcesses()         => disabledProcesses.Clear();

    // --- Unified allow dispatch (one entry is a craft Recipe — incl. book proxy — OR a
    //     ProcessorRecipe). Used by both the list rows and the detail card so the
    //     craft/process/book routing lives in one place. ---

    public bool IsEntryAllowed(Recipe r, ProcessorRecipe p) {
        if (p != null) return IsProcessAllowed(p.building);
        if (r != null) return IsAllowed(r.id); // handles the BookProxyRecipeId sentinel
        return true;
    }

    public void ToggleEntryAllowed(Recipe r, ProcessorRecipe p) {
        if (p != null)      SetProcessAllowed(p.building, !IsProcessAllowed(p.building));
        else if (r != null) SetAllowed(r.id, !IsAllowed(r.id));
    }

    // --- Expanded workstation groups (persisted; see SaveSystem) ---

    public bool IsGroupExpanded(string tile) => expandedGroups.Contains(tile);

    public void SetGroupExpanded(string tile, bool expanded) {
        if (expanded) expandedGroups.Add(tile);
        else          expandedGroups.Remove(tile);
    }

    public string[] CopyExpandedGroups() {
        var arr = new string[expandedGroups.Count];
        expandedGroups.CopyTo(arr);
        return arr;
    }

    // Replaces the whole set (used by save-load restore).
    public void SetExpandedGroups(string[] tiles) {
        expandedGroups.Clear();
        if (tiles != null) foreach (string t in tiles) expandedGroups.Add(t);
    }

    public void ClearExpandedGroups() => expandedGroups.Clear();
}
