using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Per-recipe card spawned by RecipePanel.
//
// Prefab structure:
//   RecipeDisplay (VerticalLayoutGroup, spacing=2, padding=4; ContentSizeFitter Vertical=Preferred)
//     HeaderRow   (HorizontalLayoutGroup, spacing=8, ChildForceExpand width=false)
//       DescText        (TMP, LayoutElement FlexibleWidth=1)
//       JobText         (TMP, LayoutElement PreferredWidth=130)
//       AllowButton     (Button; child TMP = AllowButtonText)
//     InputsSection   (VerticalLayoutGroup, spacing=2)
//       InputsLabel     (TMP, text="Inputs:")
//       [item labels spawned here at runtime — one per row]
//     OutputsSection  (VerticalLayoutGroup, spacing=2)
//       OutputsLabel    (TMP, text="Outputs:")
//       [item labels spawned here at runtime — one per row]
//
// Assign all [Header("UI Refs")] fields in the Inspector.

public class RecipeDisplay : MonoBehaviour {
    [Header("UI Refs")]
    public TMP_Text  descText;
    public TMP_Text  jobText;
    public Button    allowButton;
    public TMP_Text  allowButtonText;
    public Transform inputsContainer;   // InputsRow — item labels spawn here
    public Transform outputsContainer;  // OutputsRow — item labels spawn here

    Recipe recipe;
    Job    job;
    readonly List<TMP_Text> inputLabels  = new List<TMP_Text>();
    readonly List<TMP_Text> outputLabels = new List<TMP_Text>();

    public void Setup(Recipe r) {
        recipe = r;
        job    = Db.GetJobByName(recipe.job);

        string display = string.IsNullOrEmpty(recipe.description) ? recipe.tile : recipe.description;
        descText.text = display;

        allowButton.onClick.AddListener(OnClickAllow);

        SpawnItemLabels(inputsContainer,  recipe.inputs,  inputLabels,  isOutput: false);
        SpawnItemLabels(outputsContainer, recipe.outputs, outputLabels, isOutput: true);

        Refresh();
    }

    void SpawnItemLabels(Transform container, ItemQuantity[] items,
                         List<TMP_Text> labels, bool isOutput) {
        foreach (ItemQuantity iq in items) {
            var go = new GameObject("ItemLabel_" + iq.item.name);
            go.transform.SetParent(container, false);

            // RectTransform must exist before TextMeshProUGUI to stay in the Canvas hierarchy.
            go.AddComponent<RectTransform>();

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 14f;
            le.flexibleWidth   = 1f;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            // copy font settings from descText so it matches the panel style
            if (descText != null) {
                tmp.font     = descText.font;
                tmp.fontSize = descText.fontSize;
                tmp.color    = descText.color;
            }
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Truncate;

            labels.Add(tmp);
        }
    }

    public void Refresh() {
        // Job text + animal count
        int count = 0;
        if (job != null && AnimalController.instance != null)
            AnimalController.instance.jobCounts.TryGetValue(job, out count);
        jobText.text = recipe.job + " (" + count + ")";

        // Item quantity labels
        var ginv    = GlobalInventory.instance;
        var targets = InventoryController.instance?.targets;

        RefreshLabels(recipe.inputs,  inputLabels,  ginv, targets, isOutput: false);
        RefreshLabels(recipe.outputs, outputLabels, ginv, targets, isOutput: true);

        // Allow button
        bool allowed = RecipePanel.instance == null || RecipePanel.instance.IsAllowed(recipe.id);
        allowButtonText.text = allowed ? "On" : "Off";
    }

    void RefreshLabels(ItemQuantity[] items, List<TMP_Text> labels,
                       GlobalInventory ginv, System.Collections.Generic.Dictionary<int, int> targets,
                       bool isOutput) {
        for (int i = 0; i < items.Length && i < labels.Count; i++) {
            ItemQuantity iq  = items[i];
            int qty    = ginv?.Quantity(iq.item.id) ?? 0;
            int target = (targets != null && targets.TryGetValue(iq.item.id, out int t)) ? t : 0;

            string text = iq.item.name + ": "
                + ItemStack.FormatQ(qty,    iq.item.discrete)
                + " / "
                + ItemStack.FormatQ(target, iq.item.discrete);

            if (isOutput && iq.chance < 1f)
                text += " (" + Mathf.RoundToInt(iq.chance * 100f) + "%)";

            labels[i].text = text;
        }
    }

    void OnClickAllow() {
        if (RecipePanel.instance == null) return;
        bool nowAllowed = RecipePanel.instance.IsAllowed(recipe.id);
        RecipePanel.instance.SetAllowed(recipe.id, !nowAllowed);
        Refresh();
    }
}
