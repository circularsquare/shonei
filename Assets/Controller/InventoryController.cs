using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System;
using System.Linq;


public class InventoryController : MonoBehaviour {
    public static InventoryController instance {get; protected set;}
    public GlobalInventory globalInventory;
    public GameObject inventoryPanel;
    public GameObject itemDisplay; // prefab
    private World world;
    public Inventory selectedInventory; // if u click on a drawer, allows u to set what its assigned to
    public List<Inventory> selectedInventories = new List<Inventory>(); // all selected storage invs (multi-select)
    [SerializeField] private GameObject tileHighlightPrefab; // assign in inspector — prefab copy of InfoPanel's tileHighlight
    private List<GameObject> _highlightPool = new List<GameObject>();
    public Dictionary<int, bool> discoveredItems;
    public Dictionary<int, GameObject> itemDisplayGos; // keyed by itemid
    public Dictionary<int, int> targets;

    public TextMeshProUGUI inventoryTitle; // assign in inspector
    [SerializeField] private StoragePanel storagePanel; // assign in inspector
    public Dictionary<int, bool> allowedClipboard; // copied filter config for paste
    public List<Inventory> inventories = new List<Inventory>(); // list of invs
    public Dictionary<Inventory.InvType, List<Inventory>> byType = new Dictionary<Inventory.InvType, List<Inventory>>();

    void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one inv controller");}
        instance = this;
        globalInventory = new GlobalInventory();
        discoveredItems = Db.itemsFlat.ToDictionary(i => i.id, i => false); // default no items discovered
        itemDisplayGos = Db.itemsFlat.ToDictionary(i => i.id, i => default(GameObject));
        targets = Db.itemsFlat.ToDictionary(i => i.id, i => 10000); // 100 liang in fen
        // Fall animation handled by WorldController
    }

    public void AddInventory(Inventory inv) {
        inventories.Add(inv);
        if (!byType.ContainsKey(inv.invType)) byType[inv.invType] = new List<Inventory>();
        byType[inv.invType].Add(inv);
    }
    public void RemoveInventory(Inventory inv) {
        inventories.Remove(inv);
        if (byType.TryGetValue(inv.invType, out var list)) list.Remove(inv);
    }
    // Sums unreserved quantity across Floor + Storage + Liquid — the same scope FindPathItemStack searches.
    public int TotalAvailableQuantity(Item item) {
        int total = 0;
        if (byType.TryGetValue(Inventory.InvType.Floor, out var f))
            foreach (Inventory inv in f) total += inv.AvailableQuantity(item);
        if (byType.TryGetValue(Inventory.InvType.Storage, out var s))
            foreach (Inventory inv in s) total += inv.AvailableQuantity(item);
        if (byType.TryGetValue(Inventory.InvType.Liquid, out var l))
            foreach (Inventory inv in l) total += inv.AvailableQuantity(item);
        return total;
    }

    public void TickUpdate(){
        if (world == null){
            world = WorldController.instance.world;
            foreach (Item item in Db.items){
                AddItemDisplay(item);
            }
        }
        foreach (Inventory inv in inventories){
            inv.TickUpdate();
        }
        UpdateItemsDisplay();
        // Update the storage panel if it's visible
        if (storagePanel != null && storagePanel.gameObject.activeSelf)
            storagePanel.UpdateDisplay();
    }

    void AddItemDisplay(Item item){
        if (item == null){return;}

        GameObject itemDisplayGo;
        if (item.parent == null){
            itemDisplayGo = Instantiate(itemDisplay, inventoryPanel.transform);
        } else { // WARNING parent must be initiated beore child
            itemDisplayGo = Instantiate(itemDisplay, itemDisplayGos[item.parent.id].transform);
        }

        itemDisplayGos[item.id] = itemDisplayGo;
        itemDisplayGo.name = "ItemDisplay_" + item.name;
        itemDisplayGo.SetActive(discoveredItems[item.id]);

        // Global panel items: show targets, hide allow toggle
        ItemDisplay display = itemDisplayGo.GetComponent<ItemDisplay>();
        display.SetDisplayMode(ItemDisplay.DisplayMode.Global);

        UpdateItemDisplay(item);
    }
    // Returns true if this item or any descendant has quantity > 0.
    // Used to decide whether to show/highlight a parent group node in the inventory tree.
    bool HaveAnyOfChildren(Item item){
        if (globalInventory.Quantity(item.id) != 0){
            return true;
        }
        if (item.children != null){
            foreach (Item child in item.children){
                if (HaveAnyOfChildren(child)){
                    return true;
                }
            }
        }
        return false;
    }
    // Returns false if any ancestor ItemDisplay is collapsed, meaning this item should be hidden.
    bool IsVisibleInTree(Item item){
        if (item.parent == null) return true;
        ItemDisplay parentDisplay = itemDisplayGos[item.parent.id]?.GetComponent<ItemDisplay>();
        if (parentDisplay == null) return true;
        return parentDisplay.open && IsVisibleInTree(item.parent);
    }

    /// <summary>True when the global panel is being used to show a market inventory.</summary>
    private bool IsMarketMode => selectedInventory != null && selectedInventory.invType == Inventory.InvType.Market;

    void UpdateItemDisplay(Item item){
        if (item == null) return;

        // In market mode, hide items incompatible with the market inventory.
        if (IsMarketMode && !Inventory.ItemTypeCompatible(selectedInventory.invType, item)) {
            itemDisplayGos[item.id]?.SetActive(false);
            return;
        }

        bool hasItem = HaveAnyOfChildren(item);

        // Handle first-time discovery
        if (hasItem && discoveredItems[item.id] == false){
            discoveredItems[item.id] = true;
            itemDisplayGos[item.id].SetActive(IsVisibleInTree(item));
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(inventoryPanel.GetComponent<RectTransform>());
            // Refresh parent's dropdown sprite now that it has a discovered child
            if (item.parent != null && itemDisplayGos[item.parent.id] != null)
                itemDisplayGos[item.parent.id].GetComponent<ItemDisplay>()?.RefreshDropdownSprite();
        }

        // Update text if discovered (even if quantity is now 0, e.g. after Reset).
        // Respect tree collapse state — don't re-activate items whose parent is collapsed.
        if (discoveredItems[item.id]){
            itemDisplayGos[item.id]?.SetActive(IsVisibleInTree(item));
            GameObject itemDisplayGo = itemDisplayGos[item.id];
            if (itemDisplayGo == null){Debug.LogError("itemdisplaygo not found: " + item.name);return;}

            // Global panel always shows global quantities; market mode shows market quantities.
            ItemDisplay itemDisplayComp = itemDisplayGo.GetComponent<ItemDisplay>();
            string text;
            if (IsMarketMode){text = item.name + ": " + ItemStack.FormatQ(selectedInventory.Quantity(item), item.discrete);}
            else {text = item.name + ": " + ItemStack.FormatQ(globalInventory.Quantity(item), item.discrete);}
            if (itemDisplayComp.itemText != null) itemDisplayComp.itemText.text = text;

            int targetQty = IsMarketMode
                ? selectedInventory.targets[item]
                : targets[item.id];
            text = "/" + ItemStack.FormatQ(targetQty, item.discrete);
            if (itemDisplayComp.targetText != null) itemDisplayComp.targetText.text = text;

            // LoadAllowed only relevant in market mode (global panel has no toggles)
            if (IsMarketMode) {
                itemDisplayComp.LoadAllowed();
            }

            if (item.parent != null){
                UpdateItemDisplay(item.parent);
            }
        }
    }
    public void UpdateItemsDisplay(){ foreach (Item item in Db.itemsFlat){ UpdateItemDisplay(item); } }
    public void ValidateGlobalInventory() {
        var summed = new Dictionary<int, int>();
        foreach (Inventory inv in inventories) {
            foreach (ItemStack stack in inv.itemStacks) {
                if (stack.item == null || stack.quantity == 0) continue;
                if (!summed.ContainsKey(stack.item.id)) summed[stack.item.id] = 0;
                summed[stack.item.id] += stack.quantity;
            }
        }
        foreach (var kvp in globalInventory.itemAmounts) {
            int actual = summed.TryGetValue(kvp.Key, out int v) ? v : 0;
            if (actual != kvp.Value) {
                string name = Array.Find(Db.itemsFlat, i => i != null && i.id == kvp.Key)?.name ?? kvp.Key.ToString();
                Debug.LogError($"GlobalInventory mismatch: {name} — ginv={kvp.Value} actual={actual}");
            }
        }
    }
    public void SelectInventory(Inventory inv){
        selectedInventories.Clear();
        if (inv != null) selectedInventories.Add(inv);
        selectedInventory = inv;
        RefreshHighlights();

        if (inv == null) {
            // Deselect: show global view, hide storage panel
            if (inventoryTitle != null) inventoryTitle.text = "town";
            if (storagePanel != null) storagePanel.Hide();
        } else if (inv.invType == Inventory.InvType.Storage || inv.invType == Inventory.InvType.Liquid) {
            // Storage/Liquid: global panel stays as global, storage panel shows details
            if (inventoryTitle != null) inventoryTitle.text = "town";
            if (storagePanel != null) storagePanel.Show(inv);
        } else {
            // Market (or other): overwrite global panel with this inventory's view (existing behavior)
            if (inventoryTitle != null) inventoryTitle.text = inv.displayName;
            if (storagePanel != null) storagePanel.Hide();
        }

        UpdateItemsDisplay();
    }

    /// <summary>Selects multiple storage inventories at once (from drag-rect). Primary is shown in StoragePanel.</summary>
    public void SelectInventories(List<Inventory> invs, Inventory primary) {
        selectedInventories = new List<Inventory>(invs);
        selectedInventory = primary;
        RefreshHighlights();
        if (primary == null) {
            if (inventoryTitle != null) inventoryTitle.text = "town";
            if (storagePanel != null) storagePanel.Hide();
        } else if (primary.invType == Inventory.InvType.Storage || primary.invType == Inventory.InvType.Liquid) {
            if (inventoryTitle != null) inventoryTitle.text = "town";
            if (storagePanel != null) storagePanel.Show(primary);
        }
        UpdateItemsDisplay();
    }

    /// <summary>Ctrl+LMB: adds inv to selection if absent, removes it if present. Updates primary and StoragePanel.</summary>
    public void CtrlToggleInventory(Inventory inv) {
        if (inv == null) return;
        if (selectedInventories.Contains(inv)) {
            selectedInventories.Remove(inv);
            if (inv == selectedInventory)
                selectedInventory = selectedInventories.Count > 0
                    ? selectedInventories[selectedInventories.Count - 1] : null;
        } else {
            selectedInventories.Add(inv);
            selectedInventory = inv;
        }
        RefreshHighlights();
        if (selectedInventory == null) {
            if (inventoryTitle != null) inventoryTitle.text = "town";
            if (storagePanel != null) storagePanel.Hide();
        } else if (selectedInventory.invType == Inventory.InvType.Storage || selectedInventory.invType == Inventory.InvType.Liquid) {
            if (storagePanel != null) storagePanel.Show(selectedInventory);
        }
        UpdateItemsDisplay();
    }

    /// <summary>Positions highlight pool GOs over all selected inventory tiles. Grows pool on demand, never shrinks.</summary>
    private void RefreshHighlights() {
        if (tileHighlightPrefab == null) return;
        while (_highlightPool.Count < selectedInventories.Count) {
            GameObject go = Instantiate(tileHighlightPrefab);
            go.SetActive(false);
            _highlightPool.Add(go);
        }
        for (int i = 0; i < _highlightPool.Count; i++) {
            if (i < selectedInventories.Count) {
                Inventory inv = selectedInventories[i];
                _highlightPool[i].transform.position = new Vector3(inv.x, inv.y, -1);
                _highlightPool[i].SetActive(true);
            } else {
                _highlightPool[i].SetActive(false);
            }
        }
    }

    // Resets targets to defaults and clears discovery state. Called on world reset.
    public void ResetState() {
        selectedInventories.Clear();
        RefreshHighlights();
        foreach (var key in targets.Keys.ToList())
            targets[key] = 10000;
        foreach (var key in discoveredItems.Keys.ToList())
            discoveredItems[key] = false;
        foreach (var kv in itemDisplayGos)
            kv.Value?.SetActive(false);
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(inventoryPanel.GetComponent<RectTransform>());
    }

    // --- Allow All / Deny All (wire to UI buttons in editor) ---
    // These now only apply to market mode on the global panel.
    // Storage allow/deny is handled by StoragePanel.
    public void OnClickAllowAll(){
        if (selectedInventory == null) return;
        selectedInventory.AllowAll();
        UpdateItemsDisplay();
    }
    public void OnClickDenyAll(){
        if (selectedInventory == null) return;
        selectedInventory.DenyAll();
        UpdateItemsDisplay();
    }

    // --- Copy / Paste filters (Factorio-style: shift+LMB copy, shift+RMB paste) ---
    public void CopyAllowed(Inventory inv){
        allowedClipboard = new Dictionary<int, bool>(inv.allowed);
        Debug.Log($"Copied filter from {inv.displayName} at ({inv.x},{inv.y})");
    }
    public void PasteAllowed(Inventory inv){
        if (allowedClipboard == null) { Debug.Log("No filter copied yet"); return; }
        inv.PasteAllowed(allowedClipboard);
        if (selectedInventory == inv) UpdateItemsDisplay();
        Debug.Log($"Pasted filter to {inv.displayName} at ({inv.x},{inv.y})");
    }
}
