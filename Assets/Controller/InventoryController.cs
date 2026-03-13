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
    public GameObject panelInventory;
    public GameObject itemDisplay; // prefab 
    private World world;
    public Inventory selectedInventory; // if u click on a drawer, allows u to set what its assigned to
    public Dictionary<int, bool> discoveredItems;
    public Dictionary<int, GameObject> itemDisplayGos; // keyed by itemid
    public Dictionary<int, int> targets;

    public TextMeshProUGUI inventoryTitle; // assign in inspector
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
        World.OnItemFall += SpawnFallAnimation;
    }

    void OnDestroy() {
        World.OnItemFall -= SpawnFallAnimation;
    }

    void SpawnFallAnimation(int srcX, int srcY, int dstX, int dstY, Item item) {
        StartCoroutine(FallAnimCoroutine(srcX, srcY, dstX, dstY, item));
    }

    IEnumerator FallAnimCoroutine(int srcX, int srcY, int dstX, int dstY, Item item) {
        string iName = item.name.Replace(" ", "");
        Sprite sprite = Resources.Load<Sprite>($"Sprites/Items/{iName}/floor");
        sprite ??= Resources.Load<Sprite>($"Sprites/Items/{iName}/icon");
        sprite ??= Resources.Load<Sprite>("Sprites/Items/default");

        GameObject go = new GameObject("FallAnim_" + iName);
        go.transform.SetParent(WorldController.instance.transform, true);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = 65; // below floor items (sortingOrder 70)

        Vector3 start = new Vector3(srcX, srcY, 0);
        Vector3 end   = new Vector3(dstX, dstY, 0);
        go.transform.position = start;

        float dist = srcY - dstY;
        float duration = 0.4f * dist;
        float elapsed = 0f;
        while (elapsed < duration) {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            go.transform.position = Vector3.Lerp(start, end, t * t); // t² = gravity ease-in
            yield return null;
        }

        Destroy(go);
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
    public void MoveInventoryType(Inventory inv, Inventory.InvType oldType, Inventory.InvType newType) {
        if (byType.TryGetValue(oldType, out var oldList)) oldList.Remove(inv);
        if (!byType.ContainsKey(newType)) byType[newType] = new List<Inventory>();
        byType[newType].Add(inv);
    }

    public void TickUpdate(){
        if (world == null){
            world = WorldController.instance.world;
            panelInventory = GetComponent<Transform>().gameObject;
            foreach (Item item in Db.items){
                AddItemDisplay(item);
            }
        }
        foreach (Inventory inv in inventories){
            inv.TickUpdate();
        }
        UpdateItemsDisplay();
    }

    void AddItemDisplay(Item item){
        if (item == null){return;}

        GameObject itemDisplayGo;
        if (item.parent == null){
            itemDisplayGo = Instantiate(itemDisplay, panelInventory.transform);
        } else { // WARNING parent must be initiated beore child
            itemDisplayGo = Instantiate(itemDisplay, itemDisplayGos[item.parent.id].transform);
        }

        itemDisplayGos[item.id] = itemDisplayGo;
        itemDisplayGo.name = "ItemDisplay_" + item.name;
        itemDisplayGo.SetActive(discoveredItems[item.id]);
        UpdateItemDisplay(item);    
    }
    bool HaveAnyOfChildren(Item item){ // this is a temporary fix while items are not actually their parents!
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
    void UpdateItemDisplay(Item item){
        if (item == null){return;}
        // TODO: add thing at the top that indicates ur looking at a specific inventory
        bool hasItem = HaveAnyOfChildren(item);

        // Handle first-time discovery
        if (hasItem && discoveredItems[item.id] == false){
            discoveredItems[item.id] = true;
            itemDisplayGos[item.id].SetActive(true);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(panelInventory.GetComponent<RectTransform>());
        }

        // Update text if discovered (even if quantity is now 0, e.g. after Reset)
        if (discoveredItems[item.id]){
            GameObject itemDisplayGo = itemDisplayGos[item.id];
            if (itemDisplayGo == null){Debug.LogError("itemdisplaygo not found: " + item.name);return;}

            string text;
            if (selectedInventory != null){text = item.name + ": " + ItemStack.FormatQ(selectedInventory.Quantity(item), item.discrete);}
            else {text = item.name + ": " + ItemStack.FormatQ(globalInventory.Quantity(item.id), item.discrete);}
            Transform textGo = itemDisplayGo.transform.Find("HorizontalLayout/TextItem");
            if (textGo != null){textGo.gameObject.GetComponent<TMPro.TextMeshProUGUI>().text = text;}

            int targetQty = (selectedInventory?.invType == Inventory.InvType.Market)
                ? selectedInventory.targets[item]
                : targets[item.id];
            text = "/" + ItemStack.FormatQ(targetQty, item.discrete);
            Transform textTargetGo = itemDisplayGo.transform.Find("HorizontalLayout/TextItemTarget");
            if (textTargetGo != null){textTargetGo.gameObject.GetComponent<TMPro.TextMeshProUGUI>().text = text;}

            ItemDisplay itemDisplayComp = itemDisplayGo.GetComponent<ItemDisplay>();
            itemDisplayComp.LoadAllowed();

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
        bool ok = true;
        foreach (var kvp in globalInventory.itemAmounts) {
            int actual = summed.TryGetValue(kvp.Key, out int v) ? v : 0;
            if (actual != kvp.Value) {
                string name = Array.Find(Db.itemsFlat, i => i != null && i.id == kvp.Key)?.name ?? kvp.Key.ToString();
                Debug.LogError($"GlobalInventory mismatch: {name} — ginv={kvp.Value} actual={actual}");
                ok = false;
            }
        }
    }
    public void SelectInventory(Inventory inv){
        selectedInventory = inv;
        if (inventoryTitle != null)
            inventoryTitle.text = inv == null ? "town" : inv.displayName;
        UpdateItemsDisplay();
    }
}
