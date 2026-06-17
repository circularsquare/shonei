using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System;

// World-total item counter. Updated by Inventory.Produce on every physical item
// mutation; queried by UI/targets logic and by task layers that score by global
// availability (e.g. recipe picking). Group items sum their leaf descendants.
public class GlobalInventory {
    public static GlobalInventory instance {get; protected set;}

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

    public Dictionary<int, int> itemAmounts {get; protected set;}
    public Dictionary<int, int> itemCapacities {get; protected set;}
    Action<GlobalInventory> cbInventoryChanged;

    public GlobalInventory() { // this is instantiated by invcontroller
        if (instance != null) {
            Debug.LogError("there should only be one global inv");}
        instance = this;  

        itemAmounts = Db.itemsFlat.ToDictionary(i => i.id, i => 0);
    }

    public void AddItem(Item item, int quantity){
        // Group-item check is handled upstream in Inventory.AddItem — no need to re-log here.
        if (item.children != null && item.children.Length > 0) return;
        AddItem(item.id, quantity);
    }
    public void AddItem(int iid, int quantity){
        // itemAmounts is seeded from Db.itemsFlat at construction — any id not present
        // is an unknown / out-of-range id, not a missing leaf. Reject rather than create
        // a phantom entry (used to silently accept e.g. AddItem(999, 7)).
        if (!itemAmounts.ContainsKey(iid)){
            Debug.LogError($"GlobalInventory.AddItem: unknown item id {iid} (quantity={quantity}); rejecting.");
            return;
        }
        itemAmounts[iid] += quantity;
        if (cbInventoryChanged != null){
            cbInventoryChanged(this); } // make sure to add this callback thing wherever inv is changed

        // Invariant: an item we have any positive quantity of must be discovered.
        // Enforced at the source so /give, save-load with stale discovery state, and
        // any future code path that pushes items in get the same guarantee without
        // having to remember to call DiscoverItem themselves.
        var ic = InventoryController.instance;
        if (itemAmounts[iid] > 0 && ic != null && ic.discoveredItems != null
                && ic.discoveredItems.TryGetValue(iid, out bool isDiscovered) && !isDiscovered) {
            ic.DiscoverItem(Db.items[iid]);
        }
    }
    public void AddItems(ItemQuantity[] iqs, bool negate = false){
        if (negate){
            foreach (ItemQuantity iq in iqs){
                AddItem(iq.item.id, -iq.quantity);
            }
        } else {
            foreach (ItemQuantity iq in iqs){
                AddItem(iq.item.id, iq.quantity);
            }
        }
    }

    // Group-aware: routes through Quantity(Item) so callers using a name get the same
    // group-summing behaviour as the Item overload. Logs and returns 0 if the name is unknown.
    public int Quantity(string name){
        if (!Db.iidByName.TryGetValue(name, out int iid)){
            Debug.LogError($"GlobalInventory.Quantity: unknown item name '{name}'.");
            return 0;
        }
        return Quantity(iid);
    }
    // Group-aware: routes through Quantity(Item) so group ids sum their leaf descendants
    // instead of returning 0. Logs and returns 0 if the id doesn't resolve to an item.
    public int Quantity(int iid){
        if (iid < 0 || iid >= Db.items.Length || Db.items[iid] == null){
            Debug.LogError($"GlobalInventory.Quantity: unknown item id {iid}.");
            return 0;
        }
        return Quantity(Db.items[iid]);
    }
    // Group-aware: sums leaf descendants if item has children, otherwise exact lookup
    // against itemAmounts. The other overloads delegate here.
    public int Quantity(Item item){
        if (item.children == null){
            return itemAmounts.TryGetValue(item.id, out int amt) ? amt : 0;
        }
        int total = 0;
        foreach (Item child in item.children)
            total += Quantity(child);
        return total;
    }

    public bool SufficientResources(ItemQuantity[] iqs){
        foreach (ItemQuantity iq in iqs){
            if (Quantity(iq.item) < iq.quantity) return false;
        }
        return true;
    }

    // ── Fuel (generic) ──────────────────────────────────────────────────────
    // Any item with fuelValue>0 IS fuel; fuelValue is burnable energy per liang. Recipes burn
    // an abstract `fuelCost` (energy) rather than a specific fuel item, so one recipe accepts
    // coal OR wood OR charcoal at potency-scaled quantity. See SPEC-data §Fuel.

    // Total burnable energy across every in-stock fuel leaf. qty is in fen (100 fen = 1 liang),
    // fuelValue is per liang, so energy = qty/100 × fuelValue. Groups skipped (leaves carry stock).
    public float TotalFuelEnergy(){
        float energy = 0f;
        foreach (Item it in Db.itemsFlat){
            if (it == null || it.children != null || it.fuelValue <= 0f) continue;
            energy += Quantity(it) / 100f * it.fuelValue;
        }
        return energy;
    }

    // Is there at least `fuelCost` energy available to burn? fuelCost ≤ 0 → no fuel needed.
    public bool HasFuelEnergy(float fuelCost) => fuelCost <= 0f || TotalFuelEnergy() >= fuelCost;

    // Recipe-aware craftability gate: inputs in stock AND enough fuel energy. SufficientResources
    // takes a raw ItemQuantity[] with no recipe handle, so fuel can't fold into it — every
    // craft-selection site must call this instead (see Animal.PickRecipe*/ScoreCraftRecipes).
    public bool CanCraft(Recipe recipe) =>
        SufficientResources(recipe.inputs) && HasFuelEnergy(recipe.fuelCost);

    // Picks WHICH fuel to burn: the in-stock fuel leaf we hold most surplus of relative to its
    // target — symmetric to recipe selection (which produces what we hold least of), so the
    // player steers the fuel mix via the same per-item target sliders (raise coal's target →
    // coal is "kept", wood burns first). target ≤ 0 → burn freely (sentinel +∞: want none
    // stockpiled). Untracked fuel defaults to target 100, matching Task.ResolveConsumeLeaf.
    // Returns null if no fuel is in stock anywhere.
    public Item PickFuel(){
        var targets = InventoryController.instance?.targets;
        Item best = null;
        float bestScore = -1f;
        foreach (Item it in Db.itemsFlat){
            if (it == null || it.children != null || it.fuelValue <= 0f) continue;
            int qty = Quantity(it);
            if (qty <= 0) continue;
            int target = (targets != null && targets.TryGetValue(it.id, out int t)) ? t : 100;
            float score = target <= 0 ? float.PositiveInfinity : qty / (float)target;
            if (score > bestScore){ bestScore = score; best = it; }
        }
        return best;
    }

    public void RegisterCbInventoryChanged(Action<GlobalInventory> callback){
        cbInventoryChanged += callback;}
    public void UnregisterCbInventoryChanged(Action<GlobalInventory> callback){
        cbInventoryChanged -= callback;}
} 
