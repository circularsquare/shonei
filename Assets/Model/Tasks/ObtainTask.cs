using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class ObtainTask : Task {
    public ItemQuantity iq;
    public Inventory targetInv; // null = animal's main inventory; pass foodSlotInv to equip
    public ObtainTask(Animal animal, ItemQuantity iq, Inventory targetInv = null) : base(animal){
        this.iq = iq;
        this.targetInv = targetInv;
    }
    public ObtainTask(Animal animal, Item item, int quantity, Inventory targetInv = null) : base(animal){
        iq = new ItemQuantity(item, quantity);
        this.targetInv = targetInv;
    }
    public override bool Initialize(){
        (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(iq.item);
        if (itemPath == null) { return false; }
        if (targetInv != null) FetchAndReserve(iq, itemPath.tile, stack, targetInv);
        else FetchAndReserve(iq, itemPath.tile, stack);
        return true;
    }
}
