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

    Recipe          recipe;   // set for craft recipes; null for processes
    ProcessorRecipe process;  // set for processes; null for craft recipes
    Job             job;
    ItemQuantity[]  inputs;
    ItemQuantity[]  outputs;
    readonly List<TMP_Text> inputLabels  = new List<TMP_Text>();
    readonly List<TMP_Text> outputLabels = new List<TMP_Text>();

    // Craft recipe card.
    public void Setup(Recipe r) {
        recipe = r;
        job    = Db.GetJobByName(r.job);
        Build(string.IsNullOrEmpty(r.description) ? r.tile : r.description, r.inputs, r.outputs);
    }

    // Process card (passive timed conversion). No worker/job — the header shows brew time,
    // and On/Off toggles the process by building (see RecipePanel.SetProcessAllowed).
    public void Setup(ProcessorRecipe pr) {
        process = pr;
        Build(string.IsNullOrEmpty(pr.description) ? pr.building : pr.description, pr.inputs, pr.outputs);
    }

    // Allow toggle shown as the same circle/x icons as the inventory item-disallow UI
    // (Sprites/Misc/check, redx), instead of "On"/"Off" text. Loaded once, shared.
    static Sprite iconAllowed, iconDisallowed;
    static void EnsureIcons() {
        if (iconAllowed    == null) iconAllowed    = Resources.Load<Sprite>("Sprites/Misc/check");
        if (iconDisallowed == null) iconDisallowed = Resources.Load<Sprite>("Sprites/Misc/redx");
    }

    void Build(string description, ItemQuantity[] ins, ItemQuantity[] outs) {
        inputs  = ins  ?? new ItemQuantity[0];
        outputs = outs ?? new ItemQuantity[0];
        descText.text = description;

        EnsureIcons();
        // Repurpose the allow button as a bare icon: drop the wood-frame sprite styling,
        // hide the "On"/"Off" label; Refresh sets the circle/x sprite from allow state.
        if (allowButton.image != null) {
            allowButton.image.type = Image.Type.Simple;
            allowButton.image.preserveAspect = true;
        }
        if (allowButtonText != null) allowButtonText.gameObject.SetActive(false);
        allowButton.onClick.AddListener(OnClickAllow);

        // Nudge the toggle icon ~2px off the top-right corner for breathing room.
        var headerHlg = allowButton.transform.parent.GetComponent<HorizontalLayoutGroup>();
        if (headerHlg != null) headerHlg.padding = new RectOffset(headerHlg.padding.left, 2, 2, headerHlg.padding.bottom);

        SpawnItemLabels(inputsContainer,  inputs,  inputLabels,  isOutput: false);
        SpawnItemLabels(outputsContainer, outputs, outputLabels, isOutput: true);
        BuildConditionsLine();
        Refresh();
    }

    // Detail-pane extra line: where the recipe came from + how long it takes. Built once
    // (static per recipe). Skipped for processes — their header already shows time/temp.
    void BuildConditionsLine() {
        if (process != null) return;
        var parts = new List<string>();
        string unlock = ResearchSystem.instance != null ? ResearchSystem.instance.GetUnlockResearchName(recipe.id) : null;
        if (!string.IsNullOrEmpty(unlock)) parts.Add("needs " + unlock);
        if (recipe.workload > 0f) parts.Add("work " + Mathf.RoundToInt(recipe.workload));
        // Fuel is no longer a literal input (any fuelValue>0 item satisfies fuelCost), so it
        // won't show in the inputs rows — surface it here instead, in coal-equivalent energy.
        if (recipe.fuelCost > 0f) parts.Add("fuel " + Mathf.RoundToInt(recipe.fuelCost));
        if (parts.Count == 0) return;

        var go = new GameObject("Conditions", typeof(RectTransform));
        go.transform.SetParent(transform, false); // card root VLG → appended below outputs
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 14f;
        le.flexibleWidth   = 1f;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (descText != null) { tmp.font = descText.font; tmp.fontSize = descText.fontSize; tmp.color = descText.color; }
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Truncate;
        tmp.text = string.Join("   ", parts);
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
        if (process != null) {
            // Passive process: no worker count — show brew time (+ ideal temp) instead.
            jobText.text = FormatProcessHeader(process);
        } else {
            int count = 0;
            if (job != null && AnimalController.instance != null)
                AnimalController.instance.jobCounts.TryGetValue(job, out count);
            jobText.text = recipe.job + " (" + count + ")";
        }

        var ginv = GlobalInventory.instance;
        RefreshLabels(inputs,  inputLabels,  ginv, isOutput: false);
        RefreshLabels(outputs, outputLabels, ginv, isOutput: true);

        bool allowed = RecipePanel.instance == null || RecipePanel.instance.IsEntryAllowed(recipe, process);
        if (allowButton.image != null) allowButton.image.sprite = allowed ? iconAllowed : iconDisallowed;
    }

    // Process header: brew time + ideal temperature, e.g. "2d 25°" (° is now baked into m5x7).
    static string FormatProcessHeader(ProcessorRecipe pr) {
        string n = pr.processDays == Mathf.Floor(pr.processDays) ? ((int)pr.processDays).ToString() : pr.processDays.ToString("0.#");
        string s = n + "d";
        if (pr.processTempIdeal.HasValue) s += " at " + Mathf.RoundToInt(pr.processTempIdeal.Value) + "°";
        return s;
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
        var rp = RecipePanel.instance;
        if (rp == null) return;
        rp.ToggleEntryAllowed(recipe, process);
        Refresh();
    }
}
