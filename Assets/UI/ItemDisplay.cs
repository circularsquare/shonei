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
// Refresh() is the single per-mode content repaint; the tree owners decide visibility, then call it.
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
    [SerializeField] GameObject targetTextGo;   // the TargetGroup container (hosts Quantity + Slash + TargetInput)
    [SerializeField] GameObject slashGo;        // the "/" between quantity and target — leaf rows only
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

    // Cached on Start so SetTargetChromeActive doesn't GetComponent each toggle.
    private UnityEngine.UI.RectMask2D _targetInputMask;

    public void Start(){
        // gameObject.name is "ItemDisplay_<item.name>" (set by InventoryController/StoragePanel/TradingPanel).
        // Split with count=2 so item names containing '_' (e.g. "fiction_book", "book_soymilk") survive intact —
        // a naive Split('_')[1] would drop everything after the second underscore.
        item = Db.itemByName[gameObject.name.Split(new[]{'_'}, 2)[1]];
        if (itemIcon != null) itemIcon.SetItem(item);

        // Hide the InputField's chrome (bg + clipping mask) when not focused so
        // the row collapses to a single batchable Text draw. Each TMP_InputField's
        // RectMask2D otherwise creates a hard batch boundary across the whole
        // inventory — 64 inputs = 64 boundaries, even with shared atlas textures.
        // Re-enabled on focus, disabled on commit/blur. The InputField's own
        // Text child stays visible always so the current target value shows.
        if (targetInput != null) {
            // TMP_InputField puts the mask on its Text Area child, not the root.
            _targetInputMask = targetInput.GetComponentInChildren<UnityEngine.UI.RectMask2D>(true);
            targetInput.onSelect.AddListener(OnTargetSelect);
            targetInput.onDeselect.AddListener(OnTargetDeselect);
            SetTargetChromeActive(false);
        }

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
        // Both Global and Market hide targets on group items — only leaf items hold a target now
        // (a group input resolves to a concrete leaf at scoring/consumption time; there is no group
        // target). A null item (pre-Start placeholder) is treated as a leaf so the widget shows.
        bool isGroupRow      = item != null && item.IsGroup;
        bool inGlobalOrMarket = mode == DisplayMode.Global || mode == DisplayMode.Market;
        // TargetGroup hosts Quantity + Slash + TargetInput. Keep the whole group visible in
        // Global/Market so the Quantity COUNT shows for BOTH leaves and groups (a group shows
        // its summed leaf total, e.g. "200" for wood). Storage mode hides it (no qty there).
        // The leaf-only target widgets (slash + editable target + steppers) hide for group rows,
        // so a group reads as a bare count with no dangling "/target".
        // Target EDITING now lives in the full-screen GlobalInventoryPanel, so the always-visible
        // Global panel shows count only — the editable target chrome is Market-mode only here.
        bool showLeafTarget = !isGroupRow && mode == DisplayMode.Market;
        bool showToggle     = mode == DisplayMode.Storage;
        if (targetTextGo != null) targetTextGo.SetActive(inGlobalOrMarket);
        if (slashGo != null)      slashGo.SetActive(showLeafTarget);
        if (targetInput != null)  targetInput.gameObject.SetActive(showLeafTarget);
        if (targetUpGo != null)   targetUpGo.SetActive(showLeafTarget);
        if (targetDownGo != null) targetDownGo.SetActive(showLeafTarget);
        if (toggleGo != null)     toggleGo.SetActive(showToggle);
    }

    // Single per-mode content repaint. The three tree owners (InventoryController,
    // StoragePanel, TradingPanel) keep ownership of row *visibility* — their
    // discovery / class-compat / collapse rules genuinely differ — but all content
    // refresh dispatches through here, so a new mode extends this switch instead of
    // growing a fourth bespoke caller-side repaint.
    public void Refresh() {
        if (item == null) return; // pre-Start placeholder; all three builders set item eagerly
        switch (displayMode) {
            case DisplayMode.Storage:
                LoadAllowed();
                break;
            case DisplayMode.Market: {
                Inventory market = ResolveMarketInventory();
                if (market == null) return;
                if (itemText != null) itemText.text = item.name;
                if (quantityText != null)
                    quantityText.text = ItemStack.FormatQ(market.Quantity(item), item);
                // Groups don't hold meaningful market targets — only leaf items do.
                if (!item.IsGroup) {
                    int target = market.targets != null && market.targets.ContainsKey(item)
                        ? market.targets[item] : 0;
                    SetTargetDisplay(target);
                }
                break;
            }
            default: // Global
                if (itemText != null) itemText.text = item.name;
                if (quantityText != null)
                    quantityText.text = ItemStack.FormatQ(GlobalInventory.instance.Quantity(item), item);
                // Groups hold no target (and their widget is hidden) — only leaves index `targets`.
                if (!item.IsGroup) SetTargetDisplay(InventoryController.instance.targets[item.id]);
                break;
        }
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
        LayoutUtil.RebuildImmediate(GetPanelRoot());
    }

    // Resolve which inventory to use for market-target operations.
    // Prefers targetInventory (set by TradingPanel/StoragePanel), falls back to IC selected.
    private Inventory ResolveMarketInventory() {
        if (targetInventory != null && targetInventory.invType == Inventory.InvType.Market)
            return targetInventory;
        Inventory sel = InventoryController.instance.selectedInventory;
        return (sel?.invType == Inventory.InvType.Market) ? sel : null;
    }

    // Up/down buttons step the target by 1 liang (100 fen), or 10 liang on
    // Ctrl-click (UIInput.StepMultiplier). Clamped to ≥0.
    // Step by one whole unit: 1 liang for normal items, one item's worth for discrete
    // multi-weight items (unitFen) — so a stool steps by 1 stool, not 1/3 of one.
    public void OnClickTargetUp()   => AdjustTarget(+StepFen());
    public void OnClickTargetDown() => AdjustTarget(-StepFen());
    private int StepFen() => (item != null ? item.unitFen : 100) * UIInput.StepMultiplier;

    private void AdjustTarget(int deltaFen) {
        if (displayMode == DisplayMode.Storage) return;
        if (item != null && item.IsGroup) return; // groups hold no target (widget is hidden) — defensive
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
        if (item.IsGroup) return; // groups hold no target (widget is hidden) — defensive
        if (!ItemStack.TryParseQ(s, item, out int fen)) {
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
        targetInput.SetTextWithoutNotify(ItemStack.FormatQ(fenValue, item));
    }

    // Chrome toggle for the target InputField — see Start() for the rationale.
    private void OnTargetSelect(string _)   => SetTargetChromeActive(true);
    private void OnTargetDeselect(string _) => SetTargetChromeActive(false);

    // Only toggle the mask — its batch boundary is the dominant cost.
    // The bg stays on always so users still see the white box hinting at
    // typability. The bg's per-row draws are cheap (all share UnityWhite,
    // mostly batch with each other within the canvas).
    private void SetTargetChromeActive(bool active) {
        if (_targetInputMask != null) _targetInputMask.enabled = active;
    }

    private void RefreshAfterTargetChange() {
        InventoryController.instance.UpdateItemsDisplay();
        if (targetInventory != null) TradingPanel.instance?.UpdateMarketTree();
    }

    // Refreshes the allow button sprite to reflect the current tri-state across all selected inventories.
    private void LoadAllowed(){
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
