using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class BStorage : Building
{
    public int nStacks = 4;
    public ItemStack[] itemStacks;

    public BStorage(BuildingType buildingType, int x, int y) : base(buildingType, x, y)
    {
        if (buildingType.name == "drawer"){
            nStacks = 4;
        }
        itemStacks = new ItemStack[4];
        for (int i = 0; i < nStacks; i++){
            itemStacks[i] = new ItemStack(null, 0, x, y);
        }

        // for testing!!
        addItem(Db.items[1], 5);
    }

    public int addItem(Item item, int quantity){
        for (int i = 0; i < nStacks; i++){
            int? result = itemStacks[i].addItem(item, quantity);
            if (result == null){ continue; }
            int resulti = result.Value;
            if (resulti == 0){ return 0; } // successfully added item to a stack
            else {quantity = resulti;} // set quantity to remaining size to get off and keep trying
        }
        return quantity; // leftover size
    }

}