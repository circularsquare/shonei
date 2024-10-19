using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Building {
    // public enum Jobs {
    //     None, Woodcutter, Miner, Farmer
    public GameObject go;
    public int x;
    public int y;
    public BuildingType buildingType;
    public Sprite sprite;
    public Tile tile; // not really sure how this will work for multi-tile buildings...

    public Building(BuildingType buildingType, int x, int y){
        this.buildingType = buildingType;
        this.x = x;
        this.y = y;
        this.tile = World.instance.GetTileAt(x, y);
        if (buildingType.name == "drawer"){
            tile.inv = new Inventory(4, 10, Inventory.InvType.Storage, x, y); 
            // TODO: don't overwrite existing floor inventory!!
        }

        go = new GameObject();
        go.transform.position = new Vector3(x, y, 0);
        go.transform.SetParent(WorldController.instance.transform, true);
        go.name = "Building" + buildingType.name;
        
        sprite = Resources.Load<Sprite>("Sprites/Buildings/" + buildingType.name);
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Buildings/default");
        }
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite; // remember this is copy and pasted into blueprint.cs too.

        // register callback to update sprite?
    }

    // buildings no longer have inventories. tiles do.
    // public bool ContainsItem(Item item){
    //     return (inventory != null && inventory.ContainsItem(item));}
    // public bool HasSpaceForItem(Item item){
    //     return (inventory != null && inventory.HasSpaceForItem(item));}




    // public void RegisterCbAnimalChanged(Action<Animal, Job> callback){
    //     cbAnimalChanged += callback;}
    // public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){
    //     cbAnimalChanged -= callback;}
}
