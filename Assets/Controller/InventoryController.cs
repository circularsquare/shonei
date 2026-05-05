using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System;
using System.Linq;

// Global inventory data + UI for the always-visible "town" panel.
// Owns the registry of all Inventory instances (floor, storage, market, animal),
// the GlobalInventory totals, per-item production targets, item discovery state,
// and the collapsible ItemDisplay tree that shows global quantities.
// Also handles storage selection routing (single + multi-select) and the
// StoragePanel lifecycle. Market display is handled by TradingPanel.
public class InventoryController : MonoBehaviour {
    public static InventoryController instance {get; protected set;}
    public GlobalInventory globalInventory;
    public GameObject inventoryPanel;
    public GameObject itemDisplay; // ItemDisplay prefab
    private World world;
    public Inventory selectedInventory; // primary selected inv (for StoragePanel)
    public List<Inventory> selectedInventories = new List<Inventory>();
    [SerializeField] private GameObject tileHighlightPrefab;
    private List<GameObject> _highlightPool = new List<GameObject>();
    public Dictionary<int, bool> discoveredItems;
    public Dictionary<int, GameObject> itemDisplayGos;
    public Dictionary<int, int> targets; // per-item production targets in fen
    // Staged by SaveSystem on load; consumed by ItemDisplay.Start to override the JSON-default
    // open state with the player's last saved collapse state. Keyed by item name (stable across
    // id renumbering) and only holds deltas — groups matching their defaultOpen are absent.
    public Dictionary<string, bool> pendingGroupOpenOverrides;

    public TextMeshProUGUI inventoryTitle;
    [SerializeField] private StoragePanel storagePanel;
    public Dictionary<int, bool> allowedClipboard;
    public List<Inventory> inventories = new List<Inventory>();
    public Dictionary<Inventory.InvType, List<Inventory>> byType = new Dictionary<Inventory.InvType, List<Inventory>>();

    void Start(){
        if (instance != null) {
            Debug.LogError("there should only be one inv controller");}
        instance = this;
        globalInventory = new GlobalInventory();
        discoveredItems = Db.itemsFlat.ToDictionary(i => i.id, i => false);
        itemDisplayGos = Db.itemsFlat.ToDictionary(i => i.id, i => default(GameObject));
        // Books default to 1 (100 fen) — one copy of each book is plenty; scribes will skip
        // a book recipe once any exists in the world. Everything else defaults to 100 liang.
        targets = Db.itemsFlat.ToDictionary(i => i.id, i => i.itemClass == ItemClass.Book ? 100 : 10000);
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
    // Sums unreserved quantity across Floor + Storage — the same scope FindPathItemStack searches.
    public int TotalAvailableQuantity(Item item) {
        int total = 0;
        if (byType.TryGetValue(Inventory.InvType.Floor, out var f))
            foreach (Inventory inv in f) total += inv.AvailableQuantity(item);
        if (byType.TryGetValue(Inventory.InvType.Storage, out var s))
            foreach (Inventory inv in s) total += inv.AvailableQuantity(item);
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
        if (storagePanel != null && storagePanel.gameObject.activeSelf)
            storagePanel.UpdateDisplay();
        // TradingPanel market tree — same cadence, only while panel is open.
        var tp = TradingPanel.instance;
        if (tp != null && tp.gameObject.activeSelf)
            tp.UpdateMarketTree();
    }

    void AddItemDisplay(Item item){
        if (item == null){return;}

        GameObject itemDisplayGo;
        if (item.parent == null){
            itemDisplayGo = Instantiate(itemDisplay, inventoryPanel.transform);
        } else { // parent must be instantiated before child
            itemDisplayGo = Instantiate(itemDisplay, itemDisplayGos[item.parent.id].transform);
        }

        itemDisplayGos[item.id] = itemDisplayGo;
        itemDisplayGo.name = "ItemDisplay_" + item.name;
        itemDisplayGo.SetActive(discoveredItems[item.id]);

        ItemDisplay display = itemDisplayGo.GetComponent<ItemDisplay>();
        display.SetDisplayMode(ItemDisplay.DisplayMode.Global);

        UpdateItemDisplay(item);
    }
    // Returns false if any ancestor ItemDisplay is collapsed, meaning this item should be hidden.
    bool IsVisibleInTree(Item item){
        if (item.parent == null) return true;
        ItemDisplay parentDisplay = itemDisplayGos[item.parent.id]?.GetComponent<ItemDisplay>();
        if (parentDisplay == null) return true;
        return parentDisplay.open && IsVisibleInTree(item.parent);
    }

    void UpdateItemDisplay(Item item){
        if (item == null) return;

        // Discovery on first-time positive quantity is enforced at the source in
        // GlobalInventory.AddItem — by the time we get here, anything with >0 is
        // already discovered.
        //
        // Update text if discovered (even if quantity is now 0, e.g. after Reset).
        // Respect tree collapse state — don't re-activate items whose parent is collapsed.
        if (discoveredItems[item.id]){
            itemDisplayGos[item.id]?.SetActive(IsVisibleInTree(item));
            GameObject itemDisplayGo = itemDisplayGos[item.id];
            if (itemDisplayGo == null){Debug.LogError("itemdisplaygo not found: " + item.name);return;}

            ItemDisplay itemDisplayComp = itemDisplayGo.GetComponent<ItemDisplay>();
            if (itemDisplayComp.itemText != null) itemDisplayComp.itemText.text = item.name;
            if (itemDisplayComp.quantityText != null)
                itemDisplayComp.quantityText.text = ItemStack.FormatQ(globalInventory.Quantity(item), item.discrete);

            itemDisplayComp.SetTargetDisplay(targets[item.id]);

            if (item.parent != null){
                UpdateItemDisplay(item.parent);
            }
        }
    }
    public void UpdateItemsDisplay(){ foreach (Item item in Db.itemsFlat){ UpdateItemDisplay(item); } }

    // Reveals an item in the inventory tree without requiring that it has ever been produced.
    // Walks the parent chain so ancestor group nodes are revealed too — otherwise an activated
    // leaf display would be hidden by an inactive parent GO. Safe to call before the display GOs
    // have been lazily created in TickUpdate: AddItemDisplay reads discoveredItems at creation time.
    // Called by ResearchSystem when a tech unlocks recipes, so newly-unlockable outputs appear in
    // the tree even if none have been crafted yet.
    public void DiscoverItem(Item item){
        if (item == null) return;
        if (discoveredItems == null || !discoveredItems.ContainsKey(item.id)){
            Debug.LogError($"DiscoverItem: unknown item '{item?.name}' (id {item?.id})");
            return;
        }
        bool anyChanged = false;
        for (Item it = item; it != null; it = it.parent){
            if (discoveredItems[it.id]) break;
            discoveredItems[it.id] = true;
            anyChanged = true;
            if (itemDisplayGos != null && itemDisplayGos.TryGetValue(it.id, out var go) && go != null)
                go.SetActive(IsVisibleInTree(it));
        }
        if (!anyChanged) return;
        // Refresh parent dropdown sprite so the newly-revealed child makes the parent look openable.
        if (item.parent != null && itemDisplayGos != null &&
            itemDisplayGos.TryGetValue(item.parent.id, out var parentGo) && parentGo != null)
            parentGo.GetComponent<ItemDisplay>()?.RefreshDropdownSprite();
        if (inventoryPanel != null){
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(inventoryPanel.GetComponent<RectTransform>());
        }
    }

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

        if (inventoryTitle != null) inventoryTitle.text = "town";
        if (inv != null && inv.invType == Inventory.InvType.Storage) {
            if (storagePanel != null) storagePanel.Show(inv);
        } else {
            if (storagePanel != null) storagePanel.Hide();
        }

        UpdateItemsDisplay();
    }

    // Selects multiple storage inventories at once (from drag-rect). Primary is shown in StoragePanel.
    public void SelectInventories(List<Inventory> invs, Inventory primary) {
        selectedInventories = new List<Inventory>(invs);
        selectedInventory = primary;
        RefreshHighlights();
        if (primary == null) {
            if (inventoryTitle != null) inventoryTitle.text = "town";
            if (storagePanel != null) storagePanel.Hide();
        } else if (primary.invType == Inventory.InvType.Storage) {
            if (inventoryTitle != null) inventoryTitle.text = "town";
            if (storagePanel != null) storagePanel.Show(primary);
        }
        UpdateItemsDisplay();
    }

    // Ctrl+LMB: toggle inv in/out of selection. Updates primary and StoragePanel.
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
        } else if (selectedInventory.invType == Inventory.InvType.Storage) {
            if (storagePanel != null) storagePanel.Show(selectedInventory);
        }
        UpdateItemsDisplay();
    }

    // Positions highlight GOs over selected inventory tiles. Grows pool on demand, never shrinks.
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

    // Called on world reset.
    public void ResetState() {
        selectedInventories.Clear();
        RefreshHighlights();
        foreach (var key in targets.Keys.ToList())
            targets[key] = Db.items[key].itemClass == ItemClass.Book ? 100 : 10000;
        foreach (var key in discoveredItems.Keys.ToList())
            discoveredItems[key] = false;
        pendingGroupOpenOverrides = null;
        foreach (var kv in itemDisplayGos)
            kv.Value?.SetActive(false);
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(inventoryPanel.GetComponent<RectTransform>());
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
