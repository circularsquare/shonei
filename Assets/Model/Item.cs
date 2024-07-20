using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// this class holds mostly permanent attributes about an item in general.
// items don't have unique attributes. they are like resources. 
// if you want unique attributes make something else.
public class Item // : Object
{
    public int id {get; set;}
    public string name {get; set;}
    
    public bool isComposite {get; set;}
    public bool isDiscrete {get; set;}

    // just enum?
    // items should be
        // renderable (in stockpiles or sitting on the floor or desk)
            // have an item game object.
        
    // iid: int
    // name: should be able to get id/item from name for my convenience.
    // item: stores item attributes (like "isdiscrete")
    // inventories: hold item counts indexed by iid.
    // flow for adding a wood to an inventory:
        // itemCounts[iidByName["wood"]] += 1

    // void Start() { }
    // void Update() { }

    // public Item(int id = 0, string name = "none", bool isDiscrete = false){
    //     this.id = id;
    //     this.name = name;
    //     this.isDiscrete = isDiscrete;
    // }
    

}
