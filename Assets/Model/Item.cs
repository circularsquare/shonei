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

    public Item parent; 
    
    public bool isComposite {get; set;} // get rid of this


    // inventories are the ones with gameobjects and sprites, not items.
}
