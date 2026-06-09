using UnityEngine;

// Hides the Recipes toolbar button until at least one crafting station is available. Before
// that the panel would be empty (or just a lone input-less pump), which reads as broken —
// there's nothing to craft yet.
//
// Lives on the button itself and drives a CanvasGroup (alpha + raycast) rather than
// deactivating the GameObject, so its own Update keeps polling and can re-show the button
// once the threshold is met. The required CanvasGroup is auto-added when this is attached.
//
// Scene setup: attach to the RecipeToggle button. No wiring needed.
[RequireComponent(typeof(CanvasGroup))]
public class RecipeButtonGate : MonoBehaviour {
    // Crafting stations that must be available before the button appears — see
    // CountCraftingStations for what counts (a workstation with a real, input-having recipe;
    // a lone pump does not). Serialized so the threshold can be felt out in play.
    [SerializeField] int minStations = 1;

    // Recipe availability only changes on slow events (a build finishing, a research
    // unlocking, an item first discovered), so a lazy poll is plenty.
    const float PollInterval = 1f;

    CanvasGroup cg;
    float       timer;

    void Awake() {
        cg = GetComponent<CanvasGroup>();
        Apply(RecipePanel.CountCraftingStations() >= minStations);
    }

    void Update() {
        timer += Time.unscaledDeltaTime;
        if (timer < PollInterval) return;
        timer = 0f;
        Apply(RecipePanel.CountCraftingStations() >= minStations);
    }

    void Apply(bool shown) {
        cg.alpha          = shown ? 1f : 0f;
        cg.interactable   = shown;
        cg.blocksRaycasts = shown;
    }
}
