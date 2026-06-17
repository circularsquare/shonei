using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System;
using System.Linq;

// Global inventory data + UI for the always-visible "inventory" panel.
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
    public CollapsibleHeader inventoryHeader; // optional; SaveSystem reads/writes open state via saveKey
    // Outer wood-framed container (the InventoryScroll RectTransform). When the header
    // collapses, we resize this to just the header's height so the wood frame visually
    // shrinks too. Optional — leave unwired to skip the resize behaviour.
    public RectTransform inventoryScrollRect;
    private float _inventoryScrollExpandedHeight = -1f;
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
        SeedStartDiscovered();
        itemDisplayGos = Db.itemsFlat.ToDictionary(i => i.id, i => default(GameObject));
        // Per-leaf default target seeded from Item.DefaultTargetFen. Books default to 1 liang
        // (one copy is plenty; scribes skip a book recipe once any exists). Byproducts like
        // acorn/sawdust default to 10 liang so multi-product plant harvest gating can trigger.
        // Everything else defaults to 100 liang. SaveSystem reapplies persisted overrides on load.
        // Leaf items only — group items (e.g. "wood", "food") hold no target; a group input is
        // resolved to a concrete leaf at scoring/consumption time (Recipe.GeoMeanInputs,
        // Task.ResolveConsumeLeaf), so a separate group target would be a confusing parallel knob.
        targets = Db.itemsFlat.Where(i => !i.IsGroup).ToDictionary(i => i.id, i => i.DefaultTargetFen);

        if (inventoryHeader != null) inventoryHeader.onToggled += OnHeaderToggled;

        // Initial plant-menu population, now that discovery state is seeded. Null-safe so it
        // doesn't matter whether BuildPanel.Start ran before or after this (if before, its own
        // seed gate ran with stale state and this corrects it; if after, this no-ops and its
        // gate reads the seeded state directly).
        BuildPanel.instance?.RefreshPlantVisibility();
    }

    // Resizes the wood-framed scroll container so it shrinks to just the header when
    // collapsed. Captures the expanded height on first call so the original (designer-set)
    // value gets restored on expand without hardcoding it. Also re-applies visibility on
    // expand — CollapsibleHeader's bulk SetActive(true) on every later sibling activates
    // undiscovered rows for one frame before the next 0.2-second TickUpdate would correct
    // it; UpdateItemsDisplay here closes that flash window.
    void OnHeaderToggled(bool open){
        if (inventoryScrollRect != null) {
            if (_inventoryScrollExpandedHeight < 0)
                _inventoryScrollExpandedHeight = inventoryScrollRect.sizeDelta.y;
            var sd = inventoryScrollRect.sizeDelta;
            sd.y = open ? _inventoryScrollExpandedHeight : 22f;
            inventoryScrollRect.sizeDelta = sd;
        }
        if (open) UpdateItemsDisplay();
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
        // Simulation side: decay + reservation expiry. Gated behind the sim tick
        // (and thus paused with timeScale). The display refresh below is NOT —
        // see RefreshDisplay.
        foreach (Inventory inv in inventories){
            inv.TickUpdate();
        }
        RefreshDisplay();
    }

    // Pure UI refresh: lazily builds the ItemDisplay tree on first call, then repaints
    // global quantities plus any open storage / market panels. Carries NO simulation
    // side effects (no decay, no reservation expiry), so it's safe to call while paused.
    // World gen and save load both end paused, and the tick-driven TickUpdate never fires
    // while dt==0 — so those paths call this directly to populate the panel up front.
    public void RefreshDisplay(){
        if (world == null){
            if (WorldController.instance == null) return; // not in a game scene yet
            world = WorldController.instance.world;
            foreach (Item item in Db.items){
                AddItemDisplay(item);
            }
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
        // Set item now rather than relying on ItemDisplay.Start(): Start() never runs while the GO
        // is inactive (item undiscovered, or under a collapsed parent). A null item makes
        // SetTargetDisplay no-op, stranding the prefab's placeholder target text ("a") until the
        // row is first revealed AND a later tick repaints it.
        display.item = item;
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

        // Enforce visibility every tick: discovered items get the IsVisibleInTree result;
        // undiscovered items are forced inactive. Self-healing for any case where a row
        // ended up active despite being undiscovered (root items in particular had been
        // sneaking through with the prefab default "item" placeholder text).
        GameObject itemDisplayGo = itemDisplayGos[item.id];
        if (!discoveredItems[item.id]){
            if (itemDisplayGo != null && itemDisplayGo.activeSelf) itemDisplayGo.SetActive(false);
            return;
        }
        // GO may legitimately be null pre-first-TickUpdate: Start seeds discoveredItems but
        // AddItemDisplay is lazy. The first TickUpdate creates GOs and repaints, so we just
        // skip silently here. Any persistent failure would surface at Instantiate time.
        if (itemDisplayGo == null) return;
        itemDisplayGo.SetActive(IsVisibleInTree(item));

        itemDisplayGo.GetComponent<ItemDisplay>().Refresh();

        if (item.parent != null){
            UpdateItemDisplay(item.parent);
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
        if (inventoryPanel != null)
            LayoutUtil.RebuildImmediate(inventoryPanel.GetComponent<RectTransform>());
        // A newly-discovered item may be a plant's seed — reveal that plant in the build menu.
        BuildPanel.instance?.RefreshPlantVisibility();
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

        if (inventoryTitle != null) inventoryTitle.text = "inventory";
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
            if (inventoryTitle != null) inventoryTitle.text = "inventory";
            if (storagePanel != null) storagePanel.Hide();
        } else if (primary.invType == Inventory.InvType.Storage) {
            if (inventoryTitle != null) inventoryTitle.text = "inventory";
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
            if (inventoryTitle != null) inventoryTitle.text = "inventory";
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
            // Swap the prefab's plain unlit material for the overlay-ambient one so
            // these storage-selection highlights dim toward night ambient instead of
            // glaring full-bright in the dark (see UnlitOverlayAmbient.shader).
            var hlSr = go.GetComponent<SpriteRenderer>();
            var ovMat = SpriteMaterialUtil.OverlayAmbientMaterial;
            if (hlSr != null && ovMat != null) hlSr.sharedMaterial = ovMat;
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

    // Items flagged `startDiscovered` in itemsDb (e.g. water) are revealed before any production
    // or research. Walks the parent chain so ancestor group rows are activated too — without this
    // a leaf would stay hidden behind an inactive parent. Called from Start and ResetState (world
    // reset wipes discoveredItems before save data is applied, so we have to re-seed each time).
    void SeedStartDiscovered() {
        foreach (Item item in Db.itemsFlat) {
            if (!item.startDiscovered) continue;
            for (Item it = item; it != null; it = it.parent) discoveredItems[it.id] = true;
        }
    }

    // Called on world reset.
    public void ResetState() {
        selectedInventories.Clear();
        RefreshHighlights();
        // Re-seed from each item's DefaultTargetFen (same source of truth as Start) so per-item
        // JSON defaults — byproducts at 10 liang, books at 1 — survive a world reset. Hardcoding
        // 10000 here silently flattened every non-book target to 100 liang on a new world.
        foreach (var key in targets.Keys.ToList())
            targets[key] = Db.items[key].DefaultTargetFen;
        foreach (var key in discoveredItems.Keys.ToList())
            discoveredItems[key] = false;
        SeedStartDiscovered();
        // Drop plant entries whose seed is no longer discovered; the load path re-adds the valid
        // ones as it restores discovery via DiscoverItem. Prevents stale entries surviving a reload.
        BuildPanel.instance?.RefreshPlantVisibility();
        pendingGroupOpenOverrides = null;
        foreach (var kv in itemDisplayGos)
            kv.Value?.SetActive(false);
        // Hide StoragePanel so its cached rows don't keep stale Inventory refs from
        // the previous world. Next click rebinds them via Show(inv).
        if (storagePanel != null) storagePanel.Hide();
        LayoutUtil.RebuildImmediate(inventoryPanel.GetComponent<RectTransform>());
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
