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
//       [item rows spawned here at runtime — one per item, each [icon][label]]
//     OutputsSection  (VerticalLayoutGroup, spacing=2)
//       OutputsLabel    (TMP, text="Outputs:")
//       [item rows spawned here at runtime — one per item, each [icon][label]]
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

    const float RowHeight  = 16f;
    const float IconSize   = 16f;

    // Each row is [icon][label] laid out horizontally. The icon sits left of the
    // item name; the label carries name + quantities (filled in RefreshLabels).
    void SpawnItemLabels(Transform container, ItemQuantity[] items,
                         List<TMP_Text> labels, bool isOutput) {
        // The prefab's section group has childControlWidth off, which leaves our
        // rows under-sized (~100px instead of the section's 150) — that squeezes
        // the icon to a fractional width and pushes the label to a sub-pixel x,
        // blurring the m5x7 pixel font. Turn child-width control on so each row
        // fills the section.
        var vlg = container.GetComponent<VerticalLayoutGroup>();
        if (vlg != null) vlg.childControlWidth = true;

        foreach (ItemQuantity iq in items) {
            var row = new GameObject("ItemRow_" + iq.item.name, typeof(RectTransform));
            row.transform.SetParent(container, false);

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing                = 4f;
            hlg.childAlignment         = TextAnchor.MiddleLeft;
            hlg.childControlWidth      = true;
            hlg.childControlHeight     = true;
            hlg.childForceExpandWidth  = false;
            hlg.childForceExpandHeight = true;

            var rowLE = row.AddComponent<LayoutElement>();
            rowLE.preferredHeight = RowHeight;
            rowLE.flexibleWidth   = 1f;

            // Icon. ItemIcon requires an Image (added first so its Awake finds it),
            // and resolves the sprite + hover tooltip from the item itself.
            var iconGO  = new GameObject("Icon", typeof(RectTransform));
            iconGO.transform.SetParent(row.transform, false);
            var iconImg = iconGO.AddComponent<Image>();
            iconImg.preserveAspect = true; // keep pixel-art icons square, no stretch
            var iconLE  = iconGO.AddComponent<LayoutElement>();
            iconLE.preferredWidth = iconLE.preferredHeight = IconSize;
            // minWidth + no flex keeps the icon at exactly IconSize so the row's
            // layout group can't shrink it (which would land the label off-pixel).
            iconLE.minWidth     = IconSize;
            iconLE.flexibleWidth = 0f;
            iconGO.AddComponent<ItemIcon>().SetItem(iq.item);

            // Label.
            var go = new GameObject("ItemLabel_" + iq.item.name, typeof(RectTransform));
            go.transform.SetParent(row.transform, false);

            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;

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
        var ginv = GlobalInventory.instance;

        RefreshLabels(recipe.inputs,  inputLabels,  ginv, isOutput: false);
        RefreshLabels(recipe.outputs, outputLabels, ginv, isOutput: true);

        // Allow button
        bool allowed = RecipePanel.instance == null || RecipePanel.instance.IsAllowed(recipe.id);
        allowButtonText.text = allowed ? "On" : "Off";
    }

    // Row text is "name: <recipe amount> (<amount in global inventory>)". The first
    // number is what the recipe consumes (inputs) or produces (outputs) per craft;
    // the parenthesised number is how much the colony currently holds.
    void RefreshLabels(ItemQuantity[] items, List<TMP_Text> labels,
                       GlobalInventory ginv, bool isOutput) {
        for (int i = 0; i < items.Length && i < labels.Count; i++) {
            ItemQuantity iq = items[i];
            int qty = ginv?.Quantity(iq.item.id) ?? 0;

            string text = iq.item.name + ": "
                + ItemStack.FormatQ(iq.quantity, iq.item)
                + " (" + ItemStack.FormatQ(qty, iq.item) + ")";

            if (isOutput && iq.chance < 1f)
                text += " " + Mathf.RoundToInt(iq.chance * 100f) + "%";

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
