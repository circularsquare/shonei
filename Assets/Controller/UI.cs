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

    void Start() {
        if (instance != null) {
            Debug.LogError("there should only be one ui controller");}
        instance = this;
    } 
    void StartLate(){
        if (world == null){
            world = WorldController.instance.world;
            db = Db.instance; // right now this is just used to check if the db is finished loading
            // moved this to InventoryController
            
        } 
    }

    void Update() {
        if (db == null){
            StartLate();
        }
    }


}
