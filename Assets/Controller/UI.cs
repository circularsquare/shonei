using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;

public class UI : MonoBehaviour
{
    public static UI instance {get; protected set;}
    public static World world {get; protected set;}
    public static Db db {get; protected set;}

    public GameObject JobDisplay; // prefab: same sort of thing as itemCount

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one ui controller");}
        instance = this;
    } 
    void StartLate(){
        if (world == null){
            // world = WorldController.instance.world;
            // db = Db.instance; // right now this is just used to check if the db is finished loading
            // moved this to InventoryController
        } 
    }

    // have an infoPanel on right and the rest of the panels (inactive/invisible) on the left.
    // depending on what was clicked, choose the left panel.

    void Update() {
        if (world == null){
            StartLate();
        }
    }


}
