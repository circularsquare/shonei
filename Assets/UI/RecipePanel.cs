using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
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

    readonly List<RecipeDisplay> spawnedCards   = new List<RecipeDisplay>();
    readonly HashSet<int>        disabledRecipes = new HashSet<int>();

    float       refreshTimer;
    const float RefreshInterval = 0.5f;

    void Awake() {
        if (instance != null) { Debug.LogError("two RecipePanels!"); }
        instance = this;
    }

    void OnEnable() {
        Rebuild();
    }

    void Update() {
        if (Input.GetMouseButtonDown(0) && !EventSystem.current.IsPointerOverGameObject()) {
            gameObject.SetActive(false);
            return;
        }
        refreshTimer += Time.deltaTime;
        if (refreshTimer >= RefreshInterval) {
            refreshTimer = 0f;
            foreach (var card in spawnedCards) card.Refresh();
        }
    }

    public void Toggle() {
        gameObject.SetActive(!gameObject.activeSelf);
    }

    void Rebuild() {
        if (recipeListContent == null) { Debug.LogError("RecipePanel: recipeListContent not assigned"); return; }
        if (recipeDisplayPrefab == null) { Debug.LogError("RecipePanel: recipeDisplayPrefab not assigned"); return; }

        foreach (Transform child in recipeListContent) Destroy(child.gameObject);
        spawnedCards.Clear();
        refreshTimer = 0f;

        foreach (Recipe recipe in Db.recipes) {
            if (recipe == null) continue;
            // Future: filter by ResearchSystem.IsRecipeUnlocked(recipe.id)
            SpawnCard(recipe);
        }

        if (scrollRect != null) StartCoroutine(ScrollToTop());
    }

    IEnumerator ScrollToTop() {
        yield return null; // wait for Destroy() to flush and layout to rebuild
        scrollRect.verticalNormalizedPosition = 1f;
    }

    void SpawnCard(Recipe recipe) {
        var card = Instantiate(recipeDisplayPrefab, recipeListContent, false);
        card.name = "RecipeDisplay_" + recipe.id;
        card.Setup(recipe);
        spawnedCards.Add(card);
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
}
