using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class PlantController : MonoBehaviour {
    public static PlantController instance;
    private List<Plant> plants = new List<Plant>(); // list of plants
    public int np = 0; 
    public GameObject jobsPanel;

    private World world;
    public Dictionary<Job, int> jobCounts;

    // this class keeps track of all the plants
    void Start() {    
        if (instance != null) {
            Debug.LogError("there should only be one plant controller");}
        instance = this;   
        //world = WorldController.instance.world;
    }

    public void AddPlant(Plant plant){
        plants.Add(plant);
        np += 1;
    }

    public void TickUpdate(){
        foreach (Plant plant in plants){
            plant.Grow(1);
        }
    }

}