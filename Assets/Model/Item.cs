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

    // just enum?
    // items should maybe be
        // renderable (in stockpiles or sitting on the floor or desk)
            // have an item game object.
    

}
