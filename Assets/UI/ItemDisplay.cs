using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Linq;

// this is the script for a singular row item display prefab
public class ItemDisplay : MonoBehaviour {
    public Item item;
    public GameObject toggleGo;
    [System.NonSerialized] public bool open = true;
    public Sprite spriteOpen;
    public Sprite spriteCollapsed;
    public Sprite spriteLeaf; // no children
    private Image dropdownImage;

    public void Start(){
        // open = true is set at field declaration to avoid timing issues with UpdateItemDisplay running before Start()
        item = Db.itemByName[gameObject.name.Split('_')[1]];
        Transform btn = transform.Find("HorizontalLayout/ButtonDropdown");
        if (btn != null) dropdownImage = btn.GetComponent<Image>();
        RefreshDropdownSprite();
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
                InventoryController.instance.itemDisplayGos[child.id].SetActive(open);
            }
        }
        RefreshDropdownSprite();
        // Rebuild from the panel root so all parent containers reflow, not just this row.
        Canvas.ForceUpdateCanvases();
        RectTransform panelRect = InventoryController.instance.inventoryPanel.GetComponent<RectTransform>();
        LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);
    }
    public void OnClickTargetUp(){
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
        bool allowed = false;
        if (InventoryController.instance.selectedInventory != null){
            allowed = InventoryController.instance.selectedInventory.allowed[item.id];
        }
        if (allowed != toggleGo.GetComponent<Toggle>().isOn){
            toggleGo.GetComponent<Toggle>().SetIsOnWithoutNotify(allowed);}
    }
    public void SetAllowed(bool allowed){GetComponent<Toggle>().isOn = allowed;}
    
    public void OnClickAllow(){ //  allow item in inv
        if (InventoryController.instance.selectedInventory == null){return;}
        if (InventoryController.instance.selectedInventory != null){
            InventoryController.instance.selectedInventory.ToggleAllowItem(item);
        }
    }
}
