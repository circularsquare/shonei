using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class AnimalController : MonoBehaviour
{
    public static AnimalController instance {get; protected set;}
    public GameObject panelJobs;
    public GameObject textDisplay; // prefab: same sort of thing as itemCount
    private World world;

    // the purpose of this module right now is just to keep track of how many of each job there are
    // for display purposes.
    // actual animal updates are still in world.cs.

    void Start()
    {    
        if (instance != null) {
            Debug.LogError("there should only be one ani controller");}
        instance = this;   
    }

    void Update()
    {
        if (world == null){
            world = WorldController.instance.world;

            // this needs to run AFTER world has already been populated!
            // hopefully it is??
            addJobCounts();
        } 
    }
    

    void addJobCounts(){
        // register animal update callbacks
        for (int i = 0; i < world.na; i++){
            Animal ani = world.animals[i];
            ani.RegisterCbAnimalChanged(OnAnimalChanged);
        }

        panelJobs = UI.instance.transform.Find("PanelJobs").gameObject;
        // need to modify the below to look at the jobs enum rather than the Db.
        foreach(var job in new string[] {"woodcutter", "meow"}){
            GameObject textDisplayGo = Instantiate(textDisplay, panelJobs.transform);
            textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = job + ": ";
            textDisplayGo.name = "JobCount" + job;
        }
    }

    // something analogous for job change?
    // void OnInventoryChanged(Inventory inv_data) {
    //     foreach(var pair in Db.itemById){
    //         if (Math.Abs(inventory.GetAmount(pair.Key)) > 1e-9){
    //             panelInv.transform.Find("ItemCount" + pair.Value.iName).gameObject.GetComponent<TMPro.TextMeshProUGUI>().text 
    //                 = pair.Value.iName + ": " + inventory.GetAmount(pair.Key).ToString();
    //         }
    //     }        
    // }

    public void OnAnimalChanged(Animal animal_data) {
        Debug.Log("animal data changed");
        //animalCount_ui.GetComponent<TextMeshProUGUI>().text = Db.itemById[1].iName + ": " + "5";
    }


}
