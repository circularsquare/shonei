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
    public Item[] children {get; set;}
    
    public bool isComposite {get; set;} // get rid of this

    public int target = 1000000; // produce up to this number
    public int reserve = 0; // don't produce if have less than this number

    // inventories are the ones with gameobjects and sprites, not items.
}
