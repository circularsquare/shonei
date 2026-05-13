using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Manages the storage inventory detail panel.
// Shows a compact slot view (item: qty/max) and an allow/disallow sub-panel
// with the full collapsible item tree. Appears below the global inventory panel
// when a storage inventory is selected.
public class StoragePanel : MonoBehaviour {
    public static StoragePanel instance { get; private set; }

    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private Transform slotContainer;      // parent for StorageSlotDisplay rows
    [SerializeField] private Transform allowContainer;     // parent for allow-mode ItemDisplay tree
    [SerializeField] private GameObject storageSlotPrefab;  // StorageSlotDisplay prefab
    [SerializeField] private GameObject itemDisplayPrefab;  // same ItemDisplay prefab used everywhere

    private Inventory currentInv;
    // The allow tree is built lazily on first Show() and persisted across the panel's
    // entire lifetime — rebinding to the current inv on each Show() instead of rebuilding.
    // Db.items is stable (loaded once from JSON at startup), so cached rows never need
    // structural invalidation. Click cost drops from ~600–800 GO instantiations to ~46
    // SetActive + LoadAllowed calls. See SPEC-ui §StoragePanel for the contract.
    private Dictionary<int, GameObject> allowDisplayGos = new Dictionary<int, GameObject>();
    private bool _allowTreeBuilt = false;
    private List<GameObject> slotGos = new List<GameObject>();

    void Awake() {
        if (instance != null) Debug.LogError("There should only be one StoragePanel");
        instance = this;
        gameObject.SetActive(false);
    }

    // Show the panel for the given storage or liquid inventory (primary of the current selection).
    public void Show(Inventory inv) {
        if (inv == null) { Hide(); return; }
        currentInv = inv;
        gameObject.SetActive(true);
        var sel = InventoryController.instance.selectedInventories;
        if (sel.Count > 1) {
            string first = sel[0].displayName ?? "storage";
            bool allSame = sel.TrueForAll(i => (i.displayName ?? "storage") == first);
            titleText.text = $"{(allSame ? first : "storage")} x {sel.Count}";
        } else {
            titleText.text = inv.displayName ?? "storage";
        }
        PopulateSlots();
        BuildAllowTreeOnce();
        RefreshAllowTreeForInv(inv);
        // Force layout recalculation so ContentSizeFitters update before the frame renders
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(GetComponent<RectTransform>());
    }

    // Hide the panel. Cached allow-tree rows persist as inactive children (cheap;
    // rebound on the next Show). Slot rows are still destroyed since their structure
    // (one row per actual ItemStack) varies per inventory.
    public void Hide() {
        currentInv = null;
        ClearSlots();
        gameObject.SetActive(false);
    }

    // Refresh slot quantities, allow toggle states, and tree visibility.
    // Called from InventoryController.TickUpdate while the panel is active.
    // RefreshAllowTreeForInv also picks up items discovered while the panel is open
    // (research unlocks, first-time production) — they appear within one tick.
    public void UpdateDisplay() {
        if (currentInv == null) return;
        UpdateSlots();
        RefreshAllowTreeForInv(currentInv);
    }

    // --- Slot display (compact view of actual item stacks) ---

    private void PopulateSlots() {
        ClearSlots();
        var selected = InventoryController.instance.selectedInventories;

        if (selected.Count <= 1) {
            // Single inventory: show individual stacks as before
            Inventory inv = selected.Count == 1 ? selected[0] : currentInv;
            foreach (ItemStack stack in inv.itemStacks) {
                GameObject go = Instantiate(storageSlotPrefab, slotContainer);
                slotGos.Add(go);
                go.GetComponent<StorageSlotDisplay>().UpdateSlot(stack, inv.stackSize);
            }
            return;
        }

        // Multiple inventories: aggregate by item type across all selected
        var totalQty = new Dictionary<Item, int>();
        var occupiedCap = new Dictionary<Item, int>();
        var totalResSpace = new Dictionary<Item, int>();
        int totalEmptyCap = 0;

        foreach (Inventory inv in selected) {
            foreach (ItemStack stack in inv.itemStacks) {
                if (stack.item != null && stack.quantity > 0) {
                    if (!totalQty.ContainsKey(stack.item))      totalQty[stack.item]      = 0;
                    if (!occupiedCap.ContainsKey(stack.item))   occupiedCap[stack.item]   = 0;
                    if (!totalResSpace.ContainsKey(stack.item)) totalResSpace[stack.item] = 0;
                    totalQty[stack.item]      += stack.quantity;
                    occupiedCap[stack.item]   += inv.stackSize;
                    totalResSpace[stack.item] += stack.resSpace;
                } else {
                    totalEmptyCap += inv.stackSize;
                }
            }
        }

        // One row per item type, sorted by name
        var sortedItems = new List<Item>(totalQty.Keys);
        sortedItems.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));
        foreach (Item item in sortedItems) {
            GameObject go = Instantiate(storageSlotPrefab, slotContainer);
            slotGos.Add(go);
            go.GetComponent<StorageSlotDisplay>().UpdateSlot(item, totalQty[item], occupiedCap[item], totalResSpace[item]);
        }

        // One combined row for all empty capacity
        if (totalEmptyCap > 0) {
            GameObject go = Instantiate(storageSlotPrefab, slotContainer);
            slotGos.Add(go);
            go.GetComponent<StorageSlotDisplay>().UpdateSlot(null, 0, totalEmptyCap);
        }
    }

    private void UpdateSlots() {
        // Repopulate rather than update in-place: aggregated row count can change each tick
        PopulateSlots();
    }

    private void ClearSlots() {
        foreach (GameObject go in slotGos) {
            go.SetActive(false); // immediately removes from layout (Destroy is deferred to end-of-frame)
            Destroy(go);
        }
        slotGos.Clear();
    }

    // --- Allow tree (collapsible item hierarchy with toggles) ---

    // One-shot tree construction. Instantiates a row for EVERY item in Db.items
    // regardless of the current inventory's storageClass — the per-inventory
    // ItemTypeCompatible filter is applied at refresh time so the same cached tree
    // works for Default / Liquid / Book inventories. Db.items iteration order is
    // parent-before-child (same as InventoryController.AddItemDisplay relies on),
    // so parent rows always exist by the time a child needs to look up its parent.
    private void BuildAllowTreeOnce() {
        if (_allowTreeBuilt) return;
        RectTransform panelRoot = allowContainer.GetComponent<RectTransform>();

        foreach (Item item in Db.items) {
            if (item == null) continue;

            Transform parent = item.parent == null
                ? allowContainer
                : (allowDisplayGos.ContainsKey(item.parent.id) ? allowDisplayGos[item.parent.id].transform : allowContainer);

            GameObject go = Instantiate(itemDisplayPrefab, parent);
            go.name = "ItemDisplay_" + item.name;
            allowDisplayGos[item.id] = go;
            // Start inactive; RefreshAllowTreeForInv activates the right rows immediately.
            go.SetActive(false);

            ItemDisplay display = go.GetComponent<ItemDisplay>();
            display.item = item; // set immediately (Start() won't run until next frame)
            display.displayMode = ItemDisplay.DisplayMode.Storage;
            display.panelRoot = panelRoot;
            display.getDisplayGo = id => allowDisplayGos.ContainsKey(id) ? allowDisplayGos[id] : null;
            display.SetDisplayMode(ItemDisplay.DisplayMode.Storage);

            // Preempt ItemDisplay.Start() — it runs next frame, but RefreshAllowTreeForInv
            // reads `display.open` THIS frame to compute child visibility. Setting it here
            // (once, on build) also lets the user's collapse state survive across Show() calls,
            // since Start() runs only once per row lifetime.
            display.open = ItemDisplay.DefaultOpenForGroup(item);

            if (display.itemText != null) display.itemText.text = item.name;
        }
        _allowTreeBuilt = true;
    }

    // Per-Show rebind: walks every cached row and updates targetInventory, visibility,
    // and allow-toggle sprite. Also serves as the per-tick refresh from UpdateDisplay
    // (picks up newly-discovered items and any external allow/disallow changes).
    private void RefreshAllowTreeForInv(Inventory inv) {
        var discovered = InventoryController.instance.discoveredItems;
        foreach (var kvp in allowDisplayGos) {
            GameObject go = kvp.Value;
            ItemDisplay display = go.GetComponent<ItemDisplay>();
            Item item = display.item;

            display.targetInventory = inv;

            bool compat = inv.ItemTypeCompatible(item);
            bool isDiscovered = discovered.TryGetValue(item.id, out bool d) && d;
            bool visible = compat && isDiscovered && IsVisibleInAllowTree(item);
            if (go.activeSelf != visible) go.SetActive(visible);

            display.LoadAllowed();
        }
    }

    // Mirrors InventoryController.IsVisibleInTree but walks allowDisplayGos.
    private bool IsVisibleInAllowTree(Item item) {
        if (item.parent == null) return true;
        if (!allowDisplayGos.TryGetValue(item.parent.id, out var parentGo)) return true;
        ItemDisplay parentDisplay = parentGo.GetComponent<ItemDisplay>();
        if (parentDisplay == null) return true;
        return parentDisplay.open && IsVisibleInAllowTree(item.parent);
    }

    // --- Allow All / Deny All (wire to buttons in editor) ---

    public void OnClickAllowAll() {
        if (currentInv == null) return;
        foreach (Inventory inv in InventoryController.instance.selectedInventories)
            inv.AllowAll();
        UpdateDisplay();
    }

    public void OnClickDenyAll() {
        if (currentInv == null) return;
        foreach (Inventory inv in InventoryController.instance.selectedInventories)
            inv.DenyAll();
        UpdateDisplay();
    }
}
