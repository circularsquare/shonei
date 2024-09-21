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
            // todo: make the sprite look different
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;

        deliveredResources = new ItemQuantity[buildingType.costs.Length];
        for (int i = 0; i < buildingType.costs.Length; i++){
            deliveredResources[i] = new ItemQuantity(buildingType.costs[i].id, 0);
        }
        costs = buildingType.costs;

        // register callback to update sprite?
    }

    public int RecieveResource(Item item, int quantity){
            // this maybe should be using itemstacks instead of item quantitys.     

        int delivered = 0;
        for (int i = 0; i < deliveredResources.Length; i++) {
            if (deliveredResources[i].id == item.id) {
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
        return (quantity - delivered); // excess
        
    }
    public void Complete(){
        Building building = new Building(buildingType, x, y);
        tile.building = building;
        GlobalInventory.instance.AddItems(buildingType.costs, true);

        // delete blueprint
        tile.blueprint = null;
        GameObject.Destroy(go);
    }
}