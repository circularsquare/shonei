using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages the storage inventory detail panel.
/// Shows a compact slot view (item: qty/max) and an allow/disallow sub-panel
/// with the full collapsible item tree. Appears below the global inventory panel
/// when a storage inventory is selected.
/// </summary>
public class StoragePanel : MonoBehaviour {
    public static StoragePanel instance { get; private set; }

    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private Transform slotContainer;      // parent for StorageSlotDisplay rows
    [SerializeField] private Transform allowContainer;     // parent for allow-mode ItemDisplay tree
    [SerializeField] private GameObject storageSlotPrefab;  // StorageSlotDisplay prefab
    [SerializeField] private GameObject itemDisplayPrefab;  // same ItemDisplay prefab used everywhere

    private Inventory currentInv;
    private Dictionary<int, GameObject> allowDisplayGos = new Dictionary<int, GameObject>();
    private List<GameObject> slotGos = new List<GameObject>();

    void Awake() {
        if (instance != null) Debug.LogError("There should only be one StoragePanel");
        instance = this;
        gameObject.SetActive(false);
    }

    /// <summary>Show the panel for the given storage or liquid inventory (primary of the current selection).</summary>
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
        PopulateAllowTree();
        // Force layout recalculation so ContentSizeFitters update before the frame renders
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(GetComponent<RectTransform>());
    }

    /// <summary>Hide the panel and clean up dynamic children.</summary>
    public void Hide() {
        currentInv = null;
        ClearSlots();
        ClearAllowTree();
        gameObject.SetActive(false);
    }

    /// <summary>Refresh slot quantities and allow toggle states. Called from InventoryController.TickUpdate.</summary>
    public void UpdateDisplay() {
        if (currentInv == null) return;
        UpdateSlots();
        UpdateAllowToggles();
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
        int totalEmptyCap = 0;

        foreach (Inventory inv in selected) {
            foreach (ItemStack stack in inv.itemStacks) {
                if (stack.item != null && stack.quantity > 0) {
                    if (!totalQty.ContainsKey(stack.item))    totalQty[stack.item]    = 0;
                    if (!occupiedCap.ContainsKey(stack.item)) occupiedCap[stack.item] = 0;
                    totalQty[stack.item]    += stack.quantity;
                    occupiedCap[stack.item] += inv.stackSize;
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
            go.GetComponent<StorageSlotDisplay>().UpdateSlot(item, totalQty[item], occupiedCap[item]);
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

    private void PopulateAllowTree() {
        ClearAllowTree();
        RectTransform panelRoot = allowContainer.GetComponent<RectTransform>();

        // Iterate all items in Db.items (same order as InventoryController.AddItemDisplay).
        // Each item is parented to either allowContainer (root items) or its parent's GO.
        foreach (Item item in Db.items) {
            if (item == null) continue;
            // Hard filter: skip items incompatible with this inventory type
            if (!currentInv.ItemTypeCompatible(item)) continue;

            Transform parent = item.parent == null
                ? allowContainer
                : (allowDisplayGos.ContainsKey(item.parent.id) ? allowDisplayGos[item.parent.id].transform : allowContainer);

            GameObject go = Instantiate(itemDisplayPrefab, parent);
            go.name = "ItemDisplay_" + item.name;
            allowDisplayGos[item.id] = go;

            // Only show discovered items, respecting tree collapse
            bool discovered = InventoryController.instance.discoveredItems.ContainsKey(item.id)
                && InventoryController.instance.discoveredItems[item.id];
            // Also respect parent's open state — if parent is collapsed, hide this child
            bool parentOpen = item.parent == null || !allowDisplayGos.ContainsKey(item.parent.id)
                || allowDisplayGos[item.parent.id].GetComponent<ItemDisplay>().open;
            go.SetActive(discovered && parentOpen);

            ItemDisplay display = go.GetComponent<ItemDisplay>();
            display.item = item; // set immediately (Start() won't run until next frame)
            display.displayMode = ItemDisplay.DisplayMode.Storage;
            display.panelRoot = panelRoot;
            display.targetInventory = currentInv;
            display.getDisplayGo = id => allowDisplayGos.ContainsKey(id) ? allowDisplayGos[id] : null;
            display.SetDisplayMode(ItemDisplay.DisplayMode.Storage);

            // Default collapse: groups with ≤1 discovered child start collapsed
            display.open = ItemDisplay.DefaultOpenForGroup(item);

            // Set the item name text and toggle state immediately
            if (display.itemText != null) display.itemText.text = item.name;
            display.LoadAllowed();
        }
    }

    private void UpdateAllowToggles() {
        foreach (var kvp in allowDisplayGos) {
            ItemDisplay display = kvp.Value.GetComponent<ItemDisplay>();
            if (display != null) display.LoadAllowed();
        }
    }

    private void ClearAllowTree() {
        foreach (var kvp in allowDisplayGos) {
            kvp.Value.SetActive(false);
            Destroy(kvp.Value);
        }
        allowDisplayGos.Clear();
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
