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
    /// <summary>Allow state for the tri-state toggle in Storage mode.</summary>
    public enum AllowState { Allowed, Disallowed, Mixed }

    public Item item;
    public ItemIcon itemIcon;
    public TMPro.TextMeshProUGUI itemText;
    public TMPro.TMP_InputField targetInput; // user-editable target field (Global/Market modes)
    public GameObject toggleGo;  // allow/disallow button with Image child
    public Sprite spriteAllowed;
    public Sprite spriteDisallowed;
    public Sprite spriteMixed;   // shown when selection has mixed allow states
    private Image _allowImage;
    public GameObject targetUpGo;
    public GameObject targetDownGo;
    public GameObject targetTextGo;
    [System.NonSerialized] public bool open = true;
    public Sprite spriteOpen;
    public Sprite spriteCollapsed;
    public Sprite spriteLeaf; // no children
    private Image dropdownImage;

    // --- configurable per-panel fields (set at instantiation, defaults preserve existing behavior) ---
    [System.NonSerialized] public DisplayMode displayMode = DisplayMode.Global;
    [System.NonSerialized] public RectTransform panelRoot;                     // layout rebuild target; null = use inventoryPanel
    [System.NonSerialized] public Inventory targetInventory;                   // for Storage mode allow/disallow; null = use selectedInventory
    [System.NonSerialized] public System.Func<int, GameObject> getDisplayGo;   // tree lookup; null = use itemDisplayGos

    public void Start(){
        item = Db.itemByName[gameObject.name.Split('_')[1]];
        if (itemIcon != null) itemIcon.SetItem(item);
        Transform btn = transform.Find("HorizontalLayout/ButtonDropdown");
        if (btn != null) dropdownImage = btn.GetComponent<Image>();
        // Apply default collapse: groups with ≤1 discovered child start collapsed
        open = DefaultOpenForGroup(item);
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
        RefreshDropdownSprite();
        if (toggleGo != null) _allowImage = toggleGo.GetComponent<Image>();
    }

    /// <summary>Configures which UI elements are visible based on the display mode.</summary>
    public void SetDisplayMode(DisplayMode mode) {
        displayMode = mode;
        bool showTargets = mode == DisplayMode.Global || mode == DisplayMode.Market;
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

    /// <summary>Returns whether a group item should default to open.
    /// Groups with 0–1 discovered children start collapsed to reduce visual noise.
    /// Shared by both the global panel (InventoryController) and the StoragePanel allow tree.</summary>
    public static bool DefaultOpenForGroup(Item item) {
        if (item == null || item.children == null) return true; // leaf items: doesn't matter
        int discovered = 0;
        foreach (Item child in item.children) {
            if (child.IsDiscovered()) discovered++;
            if (discovered > 1) return true;
        }
        return false; // 0 or 1 discovered child → collapsed
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
        if (market != null) {
            market.targets[item] = Mathf.Max(0, market.targets[item] + deltaFen);
            market.lastTargetManualUpdateTimer = World.instance?.timer ?? float.NegativeInfinity;
            WorkOrderManager.instance?.UpdateMarketOrders(market);
        } else {
            var t = InventoryController.instance.targets;
            t[item.id] = Mathf.Max(0, t[item.id] + deltaFen);
        }
        RefreshAfterTargetChange();
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
    // Skipped while the field is focused so typed input isn't clobbered by background ticks.
    public void SetTargetDisplay(int fenValue) {
        if (targetInput == null || item == null) return;
        if (targetInput.isFocused) return;
        targetInput.SetTextWithoutNotify(ItemStack.FormatQ(fenValue, item.discrete));
    }

    private void RefreshAfterTargetChange() {
        InventoryController.instance.UpdateItemsDisplay();
        if (targetInventory != null) TradingPanel.instance?.UpdateMarketTree();
    }

    /// <summary>Refreshes the allow button sprite to reflect the current tri-state across all selected inventories.</summary>
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

    /// <summary>Returns the allow state for this item within a single inventory.
    /// For group items, Mixed if some discovered children are allowed and some aren't.</summary>
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

    /// <summary>Applies an absolute allow state to an item (and children if group) without toggle semantics.
    /// Used when fanning a primary inventory's toggle result out to secondary selected inventories.</summary>
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

    /// <summary>If every discovered child of parent is allowed, allow the whole group.</summary>
    void CheckAutoAllowParent(Inventory inv, Item parent){
        if (parent.children == null) return;
        foreach (Item sibling in parent.children)
            if (sibling.IsDiscovered() && !inv.allowed[sibling.id])
                return; // at least one discovered sibling is still off
        inv.ToggleAllowItemWithChildren(parent); // turns on parent + all children (including undiscovered)
    }
}
