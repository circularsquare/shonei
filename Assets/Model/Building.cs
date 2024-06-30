using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class Building : MonoBehaviour {
    // public enum Jobs {
    //     None, Woodcutter, Miner, Farmer

    public BuildingType buildingType;
    public Sprite sprite;

    public Building(BuildingType buildingType, int x, int y){
        this.buildingType = buildingType;
        transform.position = new Vector3(x, y, 0);

        SpriteRenderer sr = this.gameObject.AddComponent<SpriteRenderer>();
        sprite = Resources.Load<Sprite>("Sprites/Mushrooms/redMushSmall");
        sr.sprite = sprite;


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
