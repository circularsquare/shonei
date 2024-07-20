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

    public Building(BuildingType buildingType, int x, int y){
        this.buildingType = buildingType;
        this.x = x;
        this.y = y;

        go = new GameObject();
        go.transform.position = new Vector3(x, y, 0);
        go.transform.SetParent(WorldController.instance.transform, true);
        go.name = "Building" + buildingType.name;
        
        sprite = Resources.Load<Sprite>("Sprites/Buildings/" + buildingType.name);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;

        // register callback to update sprite?
    }


    // public void Work(){
    //     if (inventory == null){
    //         inventory = InventoryController.instance.inventory;
    //     }
    //     switch (job.name) {
    //         case "none":
    //             break;
    //         case "logger":
    //             inventory.AddItem("wood", 1);
    //             break;
    // }



    // public void RegisterCbAnimalChanged(Action<Animal, Job> callback){
    //     cbAnimalChanged += callback;}
    // public void UnregisterCbAnimalChanged(Action<Animal, Job> callback){
    //     cbAnimalChanged -= callback;}
}
