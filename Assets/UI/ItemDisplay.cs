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

    public Item item;
    public ItemIcon itemIcon;                  // assign in inspector (HorizontalLayout/ItemIcon)
    public TMPro.TextMeshProUGUI itemText;     // assign in inspector (HorizontalLayout/TextItem)
    public TMPro.TextMeshProUGUI targetText;   // assign in inspector (HorizontalLayout/TextItemTarget)
    public GameObject toggleGo;
    public GameObject targetUpGo;   // assign in inspector (ButtonTargetUp)
    public GameObject targetDownGo; // assign in inspector (ButtonTargetDown)
    public GameObject targetTextGo; // assign in inspector (TextItemTarget)
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
        // open = true is set at field declaration to avoid timing issues with UpdateItemDisplay running before Start()
        item = Db.itemByName[gameObject.name.Split('_')[1]];
        if (itemIcon != null) itemIcon.SetItem(item);
        Transform btn = transform.Find("HorizontalLayout/ButtonDropdown");
        if (btn != null) dropdownImage = btn.GetComponent<Image>();
        RefreshDropdownSprite();
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

    public void OnClickTargetUp(){
        if (displayMode == DisplayMode.Storage) return; // no targets in storage allow panel
        Inventory sel = InventoryController.instance.selectedInventory;
        if (sel?.invType == Inventory.InvType.Market) {
            sel.targets[item] = sel.targets[item] == 0 ? 1 : sel.targets[item] * 2;
            WorkOrderManager.instance?.UpdateMarketOrders(sel);
        } else {
            var t = InventoryController.instance.targets;
            t[item.id] = t[item.id] == 0 ? 1 : t[item.id] * 2;
        }
        InventoryController.instance.UpdateItemsDisplay();
    }
    public void OnClickTargetDown(){
        if (displayMode == DisplayMode.Storage) return; // no targets in storage allow panel
        Inventory sel = InventoryController.instance.selectedInventory;
        if (sel?.invType == Inventory.InvType.Market) {
            sel.targets[item] /= 2;
            WorkOrderManager.instance?.UpdateMarketOrders(sel);
        } else {
            InventoryController.instance.targets[item.id] /= 2;
        }
        InventoryController.instance.UpdateItemsDisplay();
    }

    public void LoadAllowed(){
        if (item == null) return; // Start() hasn't run yet
        Inventory inv = GetTargetInventory();
        bool allowed = false;
        if (inv != null){
            allowed = inv.allowed[item.id];
        }
        if (allowed != toggleGo.GetComponent<Toggle>().isOn){
            toggleGo.GetComponent<Toggle>().SetIsOnWithoutNotify(allowed);}
    }
    public void SetAllowed(bool allowed){GetComponent<Toggle>().isOn = allowed;}

    public void OnClickAllow(){ //  allow item in inv
        Inventory inv = GetTargetInventory();
        if (inv == null){return;}
        if (item.children != null && item.children.Length > 0)
            inv.ToggleAllowItemWithChildren(item);
        else {
            inv.ToggleAllowItem(item);
            // If all discovered siblings are now allowed, auto-allow parent (catches undiscovered children too)
            if (item.parent != null && inv.allowed[item.id])
                CheckAutoAllowParent(inv, item.parent);
        }
        // Refresh the appropriate display
        if (displayMode == DisplayMode.Storage) {
            StoragePanel.instance?.UpdateDisplay();
        } else {
            InventoryController.instance.UpdateItemsDisplay();
        }
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
