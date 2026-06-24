using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One row in the full-screen GlobalInventoryPanel item tree. Left→right:
//   [indent][dropdown][icon][name] | total | storage | floor | carried | market | [target +/-] | [don't-consume]
//
// Layout is authored on the prefab (Resources/Prefabs/InventoryDetailRow); this component only
// binds data + handles clicks. It is a SEPARATE, richer row than ItemDisplay (which serves the
// always-visible panel) — the location breakdown + consume toggle don't fit ItemDisplay's
// three display modes, and overloading it with a fourth mode would bloat that class.
//
// Tree shape: rows are a FLAT sibling list under the panel's scroll content (parents precede
// children, matching Db.items order). Depth is expressed as a leading indent spacer rather than
// nesting layout groups inside each other — nested LayoutGroups are a known source of pixel-font
// blur / layout churn in this project. Collapse hides descendant rows via the panel.
public class InventoryDetailRow : MonoBehaviour {
    public Item item;

    [Header("Tree")]
    [SerializeField] LayoutElement indent;            // leading spacer; width = depth × IndentPerLevel
    [SerializeField] Button dropdownButton;
    [SerializeField] Image  dropdownImage;
    [SerializeField] Sprite spriteOpen, spriteCollapsed;
    [System.NonSerialized] public bool open = true;
    public const float IndentPerLevel = 14f;

    [Header("Identity")]
    [SerializeField] ItemIcon itemIcon;
    [SerializeField] TextMeshProUGUI nameText;

    [Header("Breakdown (fen → FormatQ)")]
    [SerializeField] TextMeshProUGUI totalText, storageText, floorText, carriedText, marketText;

    [Header("Target (leaf only)")]
    [SerializeField] TMP_InputField targetInput;
    [SerializeField] Button targetUp, targetDown;

    [Header("Consume toggle")]
    [SerializeField] Button consumeButton;
    [SerializeField] Image  consumeImage;
    [SerializeField] Sprite spriteConsumable;  // shown when the item IS consumable (default)
    [SerializeField] Sprite spriteProtected;   // shown when "consume" is toggled off

    [Header("Distribution bar")]
    [SerializeField] InventoryBar bar;         // optional; visual location breakdown + target

    GlobalInventoryPanel panel;

    // Called once at spawn (panel owns the tree). depth drives the indent spacer.
    public void Init(Item item, int depth, GlobalInventoryPanel panel) {
        this.item = item;
        this.panel = panel;
        if (indent != null) indent.minWidth = indent.preferredWidth = depth * IndentPerLevel;
        if (itemIcon != null) itemIcon.SetItem(item);
        if (nameText != null) nameText.text = item.name;
        open = ItemDisplay.DefaultOpenForGroup(item);

        if (dropdownButton != null) dropdownButton.onClick.AddListener(OnClickDropdown);
        // Widen the collapse hitbox: clicking the item name toggles the group too (mirrors the
        // always-visible panel's ItemDisplay). OnClickDropdown no-ops on leaf rows, so name clicks
        // are inert for leaves. The Button is added here rather than authored on the prefab to keep
        // the toggle wiring in one place next to the dropdown.
        if (nameText != null) {
            nameText.raycastTarget = true;
            var nameButton = nameText.GetComponent<Button>();
            if (nameButton == null) nameButton = nameText.gameObject.AddComponent<Button>();
            nameButton.transition = Selectable.Transition.None; // don't tint the name on press
            nameButton.onClick.AddListener(OnClickDropdown);
        }
        // Step by one whole unit: 1 liang for normal items, one item's worth (unitFen) for discrete
        // multi-weight items — so a stool steps by 1 stool, not 1/3. Ctrl-click ×10 (StepMultiplier).
        if (targetUp != null)   targetUp.onClick.AddListener(() => AdjustTarget(+item.unitFen * UIInput.StepMultiplier));
        if (targetDown != null) targetDown.onClick.AddListener(() => AdjustTarget(-item.unitFen * UIInput.StepMultiplier));
        if (targetInput != null) targetInput.onEndEdit.AddListener(OnTargetEndEdit);
        if (consumeButton != null) consumeButton.onClick.AddListener(OnClickConsume);

        // Groups hold no target. Keep the target cells in the layout (as blank spacers) so the
        // numeric/use columns stay aligned across leaf and group rows — just hide their visuals
        // and disable interaction. SetActive(false) would collapse the cells and shift columns.
        bool isGroup = item.IsGroup;
        ConfigureTargetWidgets(!isGroup);
        // Leaf rows: dragging the bar's target marker commits a new target (groups have no editable target).
        if (bar != null && !isGroup) bar.onTargetSet = SetTargetFromBar;

        RefreshDropdownSprite();
    }

    // Configure the target column. Leaf rows: editable input + steppers. Group rows: the input
    // still shows its (read-only) summed target as plain text — interaction disabled and steppers
    // hidden — so the value matches the bar's group target. Steppers keep their layout cells either
    // way, so leaf and group columns stay aligned.
    void ConfigureTargetWidgets(bool isLeaf){
        if (targetInput != null) {
            targetInput.interactable = isLeaf;
            if (targetInput.textComponent != null) targetInput.textComponent.enabled = true; // value always shown
            if (targetInput.placeholder != null)   targetInput.placeholder.enabled = isLeaf;
        }
        if (targetUp   != null && targetUp.image   != null) { targetUp.image.enabled = isLeaf;   targetUp.interactable = isLeaf; }
        if (targetDown != null && targetDown.image != null) { targetDown.image.enabled = isLeaf; targetDown.interactable = isLeaf; }
    }

    // Per-tick content repaint (panel decides visibility, then calls this on visible rows).
    public void Refresh() {
        var ic = InventoryController.instance;
        var gi = GlobalInventory.instance;
        if (ic == null || gi == null || item == null) return;
        int total   = gi.Quantity(item);
        int storage = ic.QuantityIn(item, Inventory.InvType.Storage);
        int floor   = ic.QuantityIn(item, Inventory.InvType.Floor);
        int carried = ic.QuantityIn(item, Inventory.InvType.Animal, Inventory.InvType.Equip);
        int market  = ic.QuantityIn(item, Inventory.InvType.Market);
        if (totalText != null)   totalText.text   = ItemStack.FormatQ(total, item);
        if (storageText != null) storageText.text = ItemStack.FormatQ(storage, item);
        if (floorText != null)   floorText.text   = ItemStack.FormatQ(floor, item);
        if (carriedText != null) carriedText.text = ItemStack.FormatQ(carried, item);
        if (marketText != null)  marketText.text  = ItemStack.FormatQ(market, item);
        // Target value: leaf → its own (editable) target; group → read-only sum of discovered leaf
        // targets (same figure the bar marks against).
        if (targetInput != null && !targetInput.isFocused) {
            int tfen = item.IsGroup ? BarTarget(ic) : (ic.targets.TryGetValue(item.id, out int tv) ? tv : 0);
            targetInput.SetTextWithoutNotify(ItemStack.FormatQ(tfen, item));
        }
        // "installed" = reservoir fuel + building furnishings; bar-only, no dedicated column.
        if (bar != null) {
            int installed = ic.QuantityIn(item, Inventory.InvType.Reservoir, Inventory.InvType.Furnishing);
            bar.SetData(item, storage, floor, carried, market, installed, total, BarTarget(ic));
        }
        RefreshConsumeSprite();
    }

    // Target the bar marks/deficits against. Leaf → its own target; group → sum of *discovered*
    // leaf-descendant targets. Undiscovered leaves (e.g. oak/maple before they're ever seen) are
    // excluded so the group bar doesn't show a deficit against types the player hasn't unlocked.
    int BarTarget(InventoryController ic) {
        if (!item.IsGroup) return ic.targets.TryGetValue(item.id, out int t) ? t : 0;
        int sum = 0;
        foreach (Item leaf in item.LeafDescendants())
            if (leaf.IsDiscovered() && ic.targets.TryGetValue(leaf.id, out int lt)) sum += lt;
        return sum;
    }

    // ── Don't-consume ───────────────────────────────────────────────────
    // A group reads as protected only when EVERY leaf is protected; a mixed/clear group reads as
    // consumable, so one click protects the whole group (and a second click clears it).
    bool IsProtected() {
        var ic = InventoryController.instance;
        if (ic == null || item == null) return false;
        if (!item.IsGroup) return ic.IsConsumptionDisabled(item);
        foreach (Item leaf in item.LeafDescendants())
            if (!ic.IsConsumptionDisabled(leaf)) return false;
        return true;
    }
    void RefreshConsumeSprite() {
        if (consumeImage == null) return;
        Sprite s = IsProtected() ? spriteProtected : spriteConsumable;
        if (s != null) consumeImage.sprite = s;
    }
    void OnClickConsume() {
        var ic = InventoryController.instance;
        if (ic == null) return;
        ic.SetConsumptionDisabled(item, !IsProtected()); // group-fan handled in the setter
        panel?.RefreshAll();
    }

    // ── Target (writes the single source of truth: InventoryController.targets) ──
    void AdjustTarget(int deltaFen) {
        var ic = InventoryController.instance;
        if (ic == null || item.IsGroup || !ic.targets.ContainsKey(item.id)) return;
        ic.targets[item.id] = Mathf.Max(0, ic.targets[item.id] + deltaFen);
        Refresh();
    }
    void OnTargetEndEdit(string s) {
        var ic = InventoryController.instance;
        if (ic == null || item.IsGroup || !ic.targets.ContainsKey(item.id)) return;
        if (ItemStack.TryParseQ(s, item, out int fen)) ic.targets[item.id] = fen;
        Refresh();
    }

    // Commit a target dragged on the distribution bar (leaf only). Mirrors OnTargetEndEdit.
    void SetTargetFromBar(int fen) {
        var ic = InventoryController.instance;
        if (ic == null || item.IsGroup || !ic.targets.ContainsKey(item.id)) return;
        ic.targets[item.id] = Mathf.Max(0, fen);
        Refresh();
    }

    // ── Tree ────────────────────────────────────────────────────────────
    void OnClickDropdown() {
        if (item.children == null || item.children.Length == 0) return;
        open = !open;
        RefreshDropdownSprite();
        panel?.OnRowToggled();
    }
    public void RefreshDropdownSprite() {
        if (dropdownImage == null) return;
        bool hasKids = item.children != null && System.Array.Exists(item.children, c => c.IsDiscovered());
        // Leaf rows have no dropdown — disable the Image (a null-sprite Image renders as a white box).
        dropdownImage.enabled = hasKids;
        if (hasKids) dropdownImage.sprite = open ? spriteOpen : spriteCollapsed;
    }
}
