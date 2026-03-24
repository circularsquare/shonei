using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages the storage/liquid inventory detail panel.
/// Shows a compact slot view (item: qty/max) and an allow/disallow sub-panel
/// with the full collapsible item tree. Appears below the global inventory panel
/// when a storage or liquid inventory is selected.
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

    /// <summary>Show the panel for the given storage or liquid inventory.</summary>
    public void Show(Inventory inv) {
        if (inv == null) { Hide(); return; }
        currentInv = inv;
        gameObject.SetActive(true);
        titleText.text = inv.displayName ?? "storage";
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
        foreach (ItemStack stack in currentInv.itemStacks) {
            GameObject go = Instantiate(storageSlotPrefab, slotContainer);
            slotGos.Add(go);
            StorageSlotDisplay slot = go.GetComponent<StorageSlotDisplay>();
            slot.UpdateSlot(stack, currentInv.stackSize);
        }
    }

    private void UpdateSlots() {
        for (int i = 0; i < slotGos.Count && i < currentInv.itemStacks.Length; i++) {
            StorageSlotDisplay slot = slotGos[i].GetComponent<StorageSlotDisplay>();
            slot.UpdateSlot(currentInv.itemStacks[i], currentInv.stackSize);
        }
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
            if (!Inventory.ItemTypeCompatible(currentInv.invType, item)) continue;

            Transform parent = item.parent == null
                ? allowContainer
                : (allowDisplayGos.ContainsKey(item.parent.id) ? allowDisplayGos[item.parent.id].transform : allowContainer);

            GameObject go = Instantiate(itemDisplayPrefab, parent);
            go.name = "ItemDisplay_" + item.name;
            allowDisplayGos[item.id] = go;

            // Only show discovered items, respecting tree collapse
            bool discovered = InventoryController.instance.discoveredItems.ContainsKey(item.id)
                && InventoryController.instance.discoveredItems[item.id];
            go.SetActive(discovered);

            ItemDisplay display = go.GetComponent<ItemDisplay>();
            display.item = item; // set immediately (Start() won't run until next frame)
            display.displayMode = ItemDisplay.DisplayMode.Storage;
            display.panelRoot = panelRoot;
            display.targetInventory = currentInv;
            display.getDisplayGo = id => allowDisplayGos.ContainsKey(id) ? allowDisplayGos[id] : null;
            display.SetDisplayMode(ItemDisplay.DisplayMode.Storage);

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
        currentInv.AllowAll();
        UpdateDisplay();
    }

    public void OnClickDenyAll() {
        if (currentInv == null) return;
        currentInv.DenyAll();
        UpdateDisplay();
    }
}
