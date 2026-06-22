using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One ingredient line in the blueprint info panel: item icon + "name have/need" label, plus
// (for group-item costs locked to a concrete leaf) an X button to DISALLOW that variant for this
// blueprint, and one "cancel" chip per already-banned variant to re-allow it.
//
// Lifecycle (see StructureInfoView): Setup() does the structural build (icon, chips, button wiring)
// and runs once per row instance — on tab-show and after a disallow/allow click. UpdateDynamic()
// runs every panel tick and only touches the label text + X-button visibility, idempotently, so a
// hovered tooltip is never killed by a per-tick SetActive/Destroy (see the Tooltippable memory).
public class BlueprintCostRow : MonoBehaviour {
    [SerializeField] ItemIcon icon;
    [SerializeField] TextMeshProUGUI label;
    [SerializeField] GameObject disallowButton;     // the X; shown only for a locked group-cost leaf
    [SerializeField] Tooltippable disallowTooltip;   // on the X button — "disallow {leaf}"
    [SerializeField] Transform chipContainer;        // parent for per-banned-leaf cancel chips

    // The cancel-chip icon, loaded once. Chips are icon-only (no text), so they're built in code
    // rather than carrying a second prefab — only the row itself is scene-authored.
    static Sprite _cancelSprite;
    static Sprite CancelSprite {
        get {
            if (_cancelSprite == null) _cancelSprite = Resources.Load<Sprite>("Sprites/Misc/cancel");
            return _cancelSprite;
        }
    }

    Blueprint bp;
    int costIndex;
    System.Action onChanged;                // request a structural rebuild after a ban toggle
    readonly List<GameObject> chipGos = new List<GameObject>();

    // The authored cost item (group or leaf) — stable across slot re-locks, unlike costs[i].item.
    Item Authored => bp.structType.costs[costIndex].item;
    ItemQuantity Cost => bp.costs[costIndex];

    public void Setup(Blueprint blueprint, int index, System.Action onChangedCallback) {
        bp         = blueprint;
        costIndex  = index;
        onChanged  = onChangedCallback;

        icon?.SetItem(Cost.item);

        // X click reads the live locked leaf (not a captured one) so a re-locked slot bans the
        // right variant. DisallowLeaf no-ops on a group item, so the read is always safe.
        if (disallowButton != null) {
            Button x = disallowButton.GetComponent<Button>();
            if (x != null) {
                x.onClick.RemoveAllListeners();
                x.onClick.AddListener(() => {
                    bp.DisallowLeaf(bp.costs[costIndex].item);
                    onChanged?.Invoke();
                });
            }
        }

        BuildChips();
        UpdateDynamic();
    }

    // Per-tick: refresh the quantity label and the X-button visibility only. No GameObject churn.
    public void UpdateDynamic() {
        if (bp == null) return;
        ItemQuantity cost = Cost;
        if (label != null)
            label.text = $"{cost.item.name} {ItemStack.FormatQ(bp.inv.Quantity(cost.item), cost.item)}/{ItemStack.FormatQ(cost)}";

        // Disallow X only makes sense for a group-item cost currently locked to a concrete,
        // not-yet-banned leaf. A leaf-authored cost (Authored.IsGroup == false) never shows it.
        bool showX = Authored.IsGroup
                  && !cost.item.IsGroup
                  && !bp.disallowedLeaves.Contains(cost.item.id);
        if (disallowButton != null && disallowButton.activeSelf != showX)
            disallowButton.SetActive(showX);          // idempotent — don't toggle every tick
        if (showX && disallowTooltip != null)
            disallowTooltip.title = "disallow " + cost.item.name;
    }

    // Builds one cancel chip per banned leaf that belongs to this cost's authored group. Structural;
    // only called from Setup (tab-show / post-toggle), never per tick. Chips are built in code
    // (icon-only Button + Tooltippable) so the row needs no second prefab.
    void BuildChips() {
        foreach (GameObject go in chipGos) Destroy(go);
        chipGos.Clear();
        if (chipContainer == null) return;

        foreach (int leafId in bp.disallowedLeaves) {
            Item leaf = Db.items[leafId];
            if (leaf == null || !Inventory.MatchesItem(leaf, Authored)) continue; // chip belongs to another cost
            Item captured = leaf;
            GameObject chip = BuildChip(leaf.name + " disallowed", () => {
                bp.AllowLeaf(captured);
                onChanged?.Invoke();
            });
            chipGos.Add(chip);
        }
    }

    // Constructs one icon-only cancel chip under chipContainer: a 16×16 Button showing the cancel
    // sprite, with a Tooltippable. Mirrors the project's pixel-art UI conventions (16px art unit).
    GameObject BuildChip(string tooltip, UnityEngine.Events.UnityAction onClick) {
        GameObject go = new GameObject("cancelChip", typeof(RectTransform));
        go.transform.SetParent(chipContainer, false);

        Image img = go.AddComponent<Image>();
        img.sprite = CancelSprite;
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = 16; le.preferredHeight = 16;
        le.minWidth = 16; le.minHeight = 16; // lock size so the HLG can't shrink it to a fractional width

        Button b = go.AddComponent<Button>();
        b.targetGraphic = img;
        b.onClick.AddListener(onClick);

        Tooltippable tip = go.AddComponent<Tooltippable>();
        tip.title = tooltip;
        return go;
    }
}
