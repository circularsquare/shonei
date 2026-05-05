using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Linq;

// this is the script for a singular row item display prefab.
// DisplayMode controls which UI elements are visible:
//   Global  — targets visible, toggle hidden (used in the always-visible global inventory panel)
//   Storage — toggle visible, targets hidden (used in the StoragePanel allow sub-panel)
//   Market  — targets visible, toggle hidden (used when market overwrites the global panel)
public class ItemDisplay : MonoBehaviour {
    public enum DisplayMode { Global, Storage, Market }
    // Allow state for the tri-state toggle in Storage mode.
    public enum AllowState { Allowed, Disallowed, Mixed }

    // Cross-class contract: TradingPanel/StoragePanel assign item and read itemText/quantityText directly.
    public Item item;
    public TMPro.TextMeshProUGUI itemText;      // item name only (e.g. "silver")
    public TMPro.TextMeshProUGUI quantityText;  // current quantity, shown immediately left of the slash

    // Inspector-wired internals.
    [SerializeField] ItemIcon itemIcon;
    [SerializeField] TMPro.TMP_InputField targetInput;    // user-editable target field (Global/Market modes)
    [SerializeField] GameObject toggleGo;  // allow/disallow button with Image child
    [SerializeField] Sprite spriteAllowed;
    [SerializeField] Sprite spriteDisallowed;
    [SerializeField] Sprite spriteMixed;   // shown when selection has mixed allow states
    private Image _allowImage;
    [SerializeField] GameObject targetUpGo;
    [SerializeField] GameObject targetDownGo;
    [SerializeField] GameObject targetTextGo;
    [System.NonSerialized] public bool open = true;
    [SerializeField] Sprite spriteOpen;
    [SerializeField] Sprite spriteCollapsed;
    [SerializeField] Sprite spriteLeaf; // no children
    private Image dropdownImage;

    // --- configurable per-panel fields (set at instantiation, defaults preserve existing behavior) ---
    [System.NonSerialized] public DisplayMode displayMode = DisplayMode.Global;
    [System.NonSerialized] public RectTransform panelRoot;                     // layout rebuild target; null = use inventoryPanel
    [System.NonSerialized] public Inventory targetInventory;                   // for Storage mode allow/disallow; null = use selectedInventory
    [System.NonSerialized] public System.Func<int, GameObject> getDisplayGo;   // tree lookup; null = use itemDisplayGos

    public void Start(){
        // gameObject.name is "ItemDisplay_<item.name>" (set by InventoryController/StoragePanel/TradingPanel).
        // Split with count=2 so item names containing '_' (e.g. "fiction_book", "book_soymilk") survive intact —
        // a naive Split('_')[1] would drop everything after the second underscore.
        item = Db.itemByName[gameObject.name.Split(new[]{'_'}, 2)[1]];
        if (itemIcon != null) itemIcon.SetItem(item);
        Transform btn = transform.Find("HorizontalLayout/ButtonDropdown");
        if (btn != null) dropdownImage = btn.GetComponent<Image>();
        // Market mode keeps every group expanded — no sense in hiding tradeable leaves behind collapses.
        // Other modes start from defaultOpen, then the Global panel applies any saved override.
        if (displayMode == DisplayMode.Market) {
            open = true;
        } else {
            open = DefaultOpenForGroup(item);
            // Global panel only: restore per-group collapse state persisted by SaveSystem.
            // Storage mode's allow tree is rebuilt on every panel open, so its state is transient.
            if (displayMode == DisplayMode.Global) {
                var overrides = InventoryController.instance?.pendingGroupOpenOverrides;
                if (overrides != null && overrides.TryGetValue(item.name, out bool savedOpen))
                    open = savedOpen;
            }
            if (!open && item.children != null) {
                foreach (Item child in item.children) {
                    if (child.IsDiscovered()) {
                        GameObject go = LookupDisplayGo(child.id);
                        if (go != null) go.SetActive(false);
                    }
                }
                // No forced rebuild needed — SetActive(false) marks layout dirty,
                // and Unity auto-rebuilds before rendering.
            }
        }
        RefreshDropdownSprite();
        if (toggleGo != null) _allowImage = toggleGo.GetComponent<Image>();
    }

    // Configures which UI elements are visible based on the display mode.
    public void SetDisplayMode(DisplayMode mode) {
        displayMode = mode;
        // Market mode hides targets on group items — only leaf items hold meaningful market targets
        // (group targets never drove haul behaviour correctly, and haul orders key on leaves anyway).
        bool showTargets = mode == DisplayMode.Global || (mode == DisplayMode.Market && item != null && !item.IsGroup);
        bool showToggle  = mode == DisplayMode.Storage;
        if (targetUpGo != null)   targetUpGo.SetActive(showTargets);
        if (targetDownGo != null) targetDownGo.SetActive(showTargets);
        if (targetTextGo != null) targetTextGo.SetActive(showTargets);
        if (toggleGo != null)     toggleGo.SetActive(showToggle);
    }

    private GameObject LookupDisplayGo(int itemId) {
        if (getDisplayGo != null) return getDisplayGo(itemId);
        return InventoryController.instance.itemDisplayGos[itemId];
    }

    private RectTransform GetPanelRoot() {
        if (panelRoot != null) return panelRoot;
        return InventoryController.instance.inventoryPanel.GetComponent<RectTransform>();
    }

    private Inventory GetTargetInventory() {
        if (targetInventory != null) return targetInventory;
        return InventoryController.instance.selectedInventory;
    }

    private bool HasOpenableChildren() =>
        item != null && item.children != null && System.Array.Exists(item.children, c => c.IsDiscovered());

    // Returns whether a group item should default to open.
    // Groups start collapsed unless flagged `defaultOpen` in itemsDb.json (e.g. "food").
    // Leaf items return true — irrelevant, they have no dropdown.
    // Shared by both the global panel (InventoryController) and the StoragePanel allow tree.
    public static bool DefaultOpenForGroup(Item item) {
        if (item == null || item.children == null) return true;
        return item.defaultOpen;
    }

    public void RefreshDropdownSprite(){
        if (dropdownImage == null) return;
        if (!HasOpenableChildren()) dropdownImage.sprite = spriteLeaf;
        else dropdownImage.sprite = open ? spriteOpen : spriteCollapsed;
    }

    public void OnClickDropdown(){
        if (item == null || item.children == null || item.children.Length == 0){ return; } // don't toggle if no children
        open = !open;
        foreach (Item child in item.children){
            if (child.IsDiscovered()){ // don't toggle if undiscovered
                GameObject go = LookupDisplayGo(child.id);
                if (go != null) go.SetActive(open);
            }
        }
        RefreshDropdownSprite();
        // Rebuild from the panel root so all parent containers reflow, not just this row.
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(GetPanelRoot());
    }

    // Resolve which inventory to use for market-target operations.
    // Prefers targetInventory (set by TradingPanel/StoragePanel), falls back to IC selected.
    private Inventory ResolveMarketInventory() {
        if (targetInventory != null && targetInventory.invType == Inventory.InvType.Market)
            return targetInventory;
        Inventory sel = InventoryController.instance.selectedInventory;
        return (sel?.invType == Inventory.InvType.Market) ? sel : null;
    }

    // Up/down buttons step the target by 1 liang (100 fen). Clamped to ≥0.
    public void OnClickTargetUp()   => AdjustTarget(+100);
    public void OnClickTargetDown() => AdjustTarget(-100);

    private void AdjustTarget(int deltaFen) {
        if (displayMode == DisplayMode.Storage) return;
        Inventory market = ResolveMarketInventory();
        int newFen;
        if (market != null) {
            newFen = Mathf.Max(0, market.targets[item] + deltaFen);
            market.targets[item] = newFen;
            market.lastTargetManualUpdateTimer = World.instance?.timer ?? float.NegativeInfinity;
            WorkOrderManager.instance?.UpdateMarketOrders(market);
        } else {
            var t = InventoryController.instance.targets;
            newFen = Mathf.Max(0, t[item.id] + deltaFen);
            t[item.id] = newFen;
        }
        RefreshAfterTargetChange();
        // User-driven step — bypass the focus guard so the input reflects the change
        // even while focused (the guard only protects against background-tick clobber).
        SetTargetDisplay(newFen, force: true);
    }

    // Commits a typed value when the user finishes editing (Enter / focus loss).
    // Invalid input reverts by simply refreshing from the authoritative target.
    public void OnTargetEndEdit(string s) {
        if (displayMode == DisplayMode.Storage) return;
        if (item == null) return;
        if (!ItemStack.TryParseQ(s, item.discrete, out int fen)) {
            RefreshAfterTargetChange();
            return;
        }
        Inventory market = ResolveMarketInventory();
        if (market != null) {
            market.targets[item] = fen;
            market.lastTargetManualUpdateTimer = World.instance?.timer ?? float.NegativeInfinity;
            WorkOrderManager.instance?.UpdateMarketOrders(market);
        } else {
            InventoryController.instance.targets[item.id] = fen;
        }
        RefreshAfterTargetChange();
    }

    // Writes an authoritative fen value into the input field without firing onValueChanged.
    // Skipped while the field is focused (unless force=true) so background ticks don't
    // clobber mid-typing. User-initiated changes (scroll, buttons) pass force=true.
    public void SetTargetDisplay(int fenValue, bool force = false) {
        if (targetInput == null || item == null) return;
        if (!force && targetInput.isFocused) return;
        targetInput.SetTextWithoutNotify(ItemStack.FormatQ(fenValue, item.discrete));
    }

    private void RefreshAfterTargetChange() {
        InventoryController.instance.UpdateItemsDisplay();
        if (targetInventory != null) TradingPanel.instance?.UpdateMarketTree();
    }

    // Refreshes the allow button sprite to reflect the current tri-state across all selected inventories.
    public void LoadAllowed(){
        if (item == null) return; // Start() hasn't run yet
        // _allowImage may not be cached yet if StoragePanel sets item before Start() runs; try to fetch it now
        if (_allowImage == null && toggleGo != null) _allowImage = toggleGo.GetComponent<Image>();
        if (_allowImage == null) return;
        AllowState state = ComputeAllowState();
        Sprite sprite = state == AllowState.Allowed    ? spriteAllowed
                      : state == AllowState.Disallowed ? spriteDisallowed
                      : spriteMixed;
        if (sprite != null) _allowImage.sprite = sprite;
    }

    private AllowState ComputeAllowState() {
        var selected = InventoryController.instance.selectedInventories;
        if (selected.Count == 0) {
            Inventory inv = GetTargetInventory();
            if (inv == null) return AllowState.Disallowed;
            return GetItemAllowState(inv);
        }
        bool anyAllowed = false, anyDisallowed = false;
        foreach (Inventory inv in selected) {
            AllowState s = GetItemAllowState(inv);
            if (s == AllowState.Mixed) return AllowState.Mixed;
            if (s == AllowState.Allowed) anyAllowed    = true;
            else                         anyDisallowed = true;
            if (anyAllowed && anyDisallowed) return AllowState.Mixed;
        }
        return anyAllowed ? AllowState.Allowed : AllowState.Disallowed;
    }

    // Returns the allow state for this item within a single inventory.
    // For group items, Mixed if some discovered children are allowed and some aren't.
    private AllowState GetItemAllowState(Inventory inv) {
        if (item.children == null || item.children.Length == 0)
            return inv.allowed[item.id] ? AllowState.Allowed : AllowState.Disallowed;
        bool anyAllowed = false, anyDisallowed = false;
        foreach (Item child in item.children) {
            if (!child.IsDiscovered()) continue;
            if (inv.allowed[child.id]) anyAllowed    = true;
            else                       anyDisallowed = true;
            if (anyAllowed && anyDisallowed) return AllowState.Mixed;
        }
        return anyAllowed ? AllowState.Allowed : AllowState.Disallowed;
    }

    public void OnClickAllow(){ //  allow item in inv
        var selected = InventoryController.instance.selectedInventories;
        // Mixed or Disallowed → allow all; Allowed → disallow all
        AllowState current = selected.Count > 0 ? ComputeAllowState() : AllowState.Disallowed;
        bool targetState = current != AllowState.Allowed;

        if (selected.Count == 0) {
            Inventory inv = GetTargetInventory();
            if (inv == null) return;
            ApplyAllowState(inv, item, targetState);
        } else {
            foreach (Inventory inv in selected)
                ApplyAllowState(inv, item, targetState);
        }

        // Refresh the appropriate display
        if (displayMode == DisplayMode.Storage) {
            StoragePanel.instance?.UpdateDisplay();
        } else {
            InventoryController.instance.UpdateItemsDisplay();
        }
    }

    // Applies an absolute allow state to an item (and children if group) without toggle semantics.
    // Used when fanning a primary inventory's toggle result out to secondary selected inventories.
    private void ApplyAllowState(Inventory inv, Item item, bool state) {
        if (item.children != null && item.children.Length > 0) {
            SetAllowStateRecursive(inv, item, state);
        } else {
            if (state) inv.AllowItem(item);
            else       inv.DisallowItem(item);
            if (item.parent != null && state)
                CheckAutoAllowParent(inv, item.parent);
        }
    }

    private void SetAllowStateRecursive(Inventory inv, Item item, bool state) {
        if (state) inv.AllowItem(item);
        else       inv.DisallowItem(item);
        if (item.children != null)
            foreach (Item child in item.children)
                SetAllowStateRecursive(inv, child, state);
    }

    // If every discovered child of parent is allowed, allow the whole group.
    void CheckAutoAllowParent(Inventory inv, Item parent){
        if (parent.children == null) return;
        foreach (Item sibling in parent.children)
            if (sibling.IsDiscovered() && !inv.allowed[sibling.id])
                return; // at least one discovered sibling is still off
        inv.ToggleAllowItemWithChildren(parent); // turns on parent + all children (including undiscovered)
    }
}
