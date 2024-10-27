using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Blueprint {
    public GameObject go;
    public int x;
    public int y;
    public BuildingType buildingType;
    public Sprite sprite;
    public Tile tile; // not really sure how this will work for multi-tile buildings...

    public ItemQuantity[] deliveredResources;
    public ItemQuantity[] costs;
    public float constructionProgress; // todo: implement

    public Blueprint(BuildingType buildingType, int x, int y){
        this.buildingType = buildingType;
        this.x = x;
        this.y = y;
        this.tile = World.instance.GetTileAt(x, y);

        go = new GameObject();
        go.transform.position = new Vector3(x, y, 0);
        go.transform.SetParent(WorldController.instance.transform, true);
        go.name = "BuildingBlueprint" + buildingType.name;
        
        sprite = Resources.Load<Sprite>("Sprites/Buildings/" + buildingType.name);
        if (sprite == null || sprite.texture == null){
            sprite = Resources.Load<Sprite>("Sprites/Buildings/default");}
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = new Color(sr.color.r, sr.color.g, sr.color.b, 0.5f); // blueprint half alpha

        deliveredResources = new ItemQuantity[buildingType.costs.Length];
        for (int i = 0; i < buildingType.costs.Length; i++){
            deliveredResources[i] = new ItemQuantity(buildingType.costs[i].item, 0);
        }
        costs = buildingType.costs;

        // register callback to update sprite?
    }

    public int ReceiveResource(Item item, int quantity){
        // this maybe should be using itemstacks instead of item quantitys.     

        int delivered = 0;
        for (int i = 0; i < deliveredResources.Length; i++) {
            if (deliveredResources[i].item == item) {
                delivered = Math.Min(quantity, costs[i].quantity - deliveredResources[i].quantity);
                deliveredResources[i].quantity += delivered;
                break;
            }
        }
        // check if construction is complete 
        for (int i = 0; i < deliveredResources.Length; i++) {
            if (deliveredResources[i].quantity < costs[i].quantity) {
                break;
            }
            if (i == deliveredResources.Length - 1) { Complete(); }
        }
        return delivered;
        
    }
    public void Complete(){
        Building building = new Building(buildingType, x, y);
        tile.building = building;
        GlobalInventory.instance.AddItems(buildingType.costs, true);

        // delete blueprint
        tile.blueprint = null;
        GameObject.Destroy(go);
    }


    public string GetProgress(){
        string progress = "";
        for (int i = 0; i < deliveredResources.Length; i++) {
            progress += (deliveredResources[i].ItemName() 
                + deliveredResources[i].quantity.ToString() 
                + "/" + costs[i].quantity.ToString());
        }
        return progress;
    }
}