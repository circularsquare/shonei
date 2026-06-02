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

    float       refreshTimer;
    const float RefreshInterval = 0.5f;

    void Awake() {
        if (instance != null) { Debug.LogError("two RecipePanels!"); }
        instance = this;
        UI.RegisterExclusive(gameObject);
        if (closeButton != null) closeButton.onClick.AddListener(() => gameObject.SetActive(false));
    }

    void OnEnable() {
        Rebuild();
    }

    void Update() {
        refreshTimer += Time.deltaTime;
        if (refreshTimer >= RefreshInterval) {
            refreshTimer = 0f;
            foreach (var group in spawnedGroups) group.RefreshVisibleCards();
        }
    }

    public void Toggle() {
        if (gameObject.activeSelf) gameObject.SetActive(false);
        else UI.OpenExclusive(gameObject);
    }

    void Rebuild() {
        if (recipeListContent == null) { Debug.LogError("RecipePanel: recipeListContent not assigned"); return; }
        if (recipeDisplayPrefab == null) { Debug.LogError("RecipePanel: recipeDisplayPrefab not assigned"); return; }

        // Clears spawned GameObjects only — NOT expandedGroups (it must survive Rebuild
        // so Phase-5 save restore is honoured on the next OnEnable).
        foreach (Transform child in recipeListContent) Destroy(child.gameObject);
        spawnedGroups.Clear();
        refreshTimer = 0f;

        // Group unlocked recipes by workstation (recipe.tile), preserving first-seen
        // order so the list order is the authored recipesDb order.
        var order   = new List<string>();
        var byTile  = new Dictionary<string, List<Recipe>>();
        foreach (Recipe recipe in Db.recipes) {
            if (recipe == null) continue;
            if (ResearchSystem.instance != null && !ResearchSystem.instance.IsRecipeUnlocked(recipe.id)) continue;
            string tile = recipe.tile ?? "(none)";
            if (!byTile.TryGetValue(tile, out var list)) {
                list = new List<Recipe>();
                byTile[tile] = list;
                order.Add(tile);
            }
            list.Add(recipe);
        }

        foreach (string tile in order) SpawnGroup(tile, byTile[tile]);

        if (scrollRect != null) StartCoroutine(ScrollToTop());
    }

    IEnumerator ScrollToTop() {
        yield return null; // wait for Destroy() to flush
        // Settle nested fitters in one pass so restored-expanded groups don't pop.
        LayoutRebuilder.ForceRebuildLayoutImmediate(recipeListContent as RectTransform);
        scrollRect.verticalNormalizedPosition = 1f;
    }

    void SpawnGroup(string tile, List<Recipe> recipes) {
        StructType st = (Db.structTypeByName != null && Db.structTypeByName.TryGetValue(tile, out var t)) ? t : null;

        var go = new GameObject("RecipeGroup_" + tile, typeof(RectTransform));
        go.transform.SetParent(recipeListContent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2((recipeDisplayPrefab.transform as RectTransform).rect.width, 0f);

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
        group.Setup(tile, st, recipes, recipeDisplayPrefab, IsGroupExpanded(tile));
        spawnedGroups.Add(group);
    }

    // --- Allow / disable ---

    public bool IsAllowed(int recipeId) => !disabledRecipes.Contains(recipeId);

    public void SetAllowed(int recipeId, bool allowed) {
        if (allowed) disabledRecipes.Remove(recipeId);
        else         disabledRecipes.Add(recipeId);
    }

    public int  DisabledCount                    => disabledRecipes.Count;
    public void CopyDisabledIds(int[] dest)      => disabledRecipes.CopyTo(dest);
    public void ClearDisabled()                  => disabledRecipes.Clear();

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
