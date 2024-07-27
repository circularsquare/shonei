using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class AnimalController : MonoBehaviour
{
    public static AnimalController instance {get; protected set;}
    public Animal[] animals;
    public int na = 0; 
    private int maxna = 1000;
    public GameObject jobsPanel;

    private World world;
    public Dictionary<Job, int> jobCounts;

    // this class keeps track of all the animals and adds animals and such

    void Start() {    
        if (instance != null) {
            Debug.LogError("there should only be one ani controller");}
        instance = this;   

        animals = new Animal[maxna];
        jobCounts = new Dictionary<Job, int>();
        jobCounts.Add(Db.jobs[0], 0); 

        for (int i = 0; i < 10; i++){
            AddAnimal();
        }
    }

    void Update() {
        if (world == null){
            world = WorldController.instance.world;
            addJobCounts();  // this needs to run AFTER world has already been populated!
        } 
    }

    public void Work(){ // called on a timer from World.cs
        for (int a = 0; a < na; a++){ // later, change the animal work method to not be a timer and instead track individual animal workloads
            animals[a].Work(); 
        }
    }

    public void AddAnimal(int x = 10, int y = 2, Job job = null){
        GameObject animalPrefab = Resources.Load<GameObject>("Prefabs/Animal");
        GameObject go = GameObject.Instantiate(animalPrefab, new Vector3(x, y, 0), Quaternion.identity);
        Animal animal = go.AddComponent<Animal>(); // connect to animal script
        animal.x = x;
        animal.y = y; 
        animal.RegisterCbAnimalChanged(OnAnimalChanged);
        animal.transform.SetParent(transform);
        animals[na] = animal;
        jobCounts[Db.jobs[0]] += 1;
        na += 1;
    }

    public void AddJob(string jobstr, int n = 1){
        if (n > 0) {
            for (int a = 0; a < maxna; a++){
                if (n == 0){return;}
                if (animals[a] != null && animals[a].job.id == 0){ // if null
                    animals[a].SetJob(jobstr);
                    n -= 1;
                }
            }
            Debug.Log("no free mice!"); // only fires if doesn't return early
        } else if (n < 0) {
            for (int a = 0; a < maxna; a++){
                if (n == 0){return;}
                if (animals[a] != null && animals[a].job.name == jobstr){
                    animals[a].SetJob("none");
                    n += 1;
                }
            }
            Debug.Log("no more mice to fire!");
        }
    }

    public void OnAnimalChanged(Animal ani, Job oldJob) {
        Job newJob = ani.job;
        if (!jobCounts.ContainsKey(newJob)){
            jobCounts.Add(newJob, 1);
        } else {
            jobCounts[newJob] += 1;
        }
        if (!jobCounts.ContainsKey(oldJob)){
            jobCounts.Add(oldJob, -1);
        } else {
            jobCounts[oldJob] -= 1;
        } 

        if (jobCounts[oldJob] < 0 && oldJob.id != 0){
            Debug.LogError("have negative quantity of a job!");
        }
        updateJobCount(oldJob);
        updateJobCount(newJob);
    }

    // this maybe probably be an interface or something (shared code w inv controller)
    // updates the number of mice with this job in the ui

    void addJobCounts(){
        jobsPanel = UI.instance.transform.Find("JobsPanel").gameObject;
        foreach(Job job in Db.jobs){
            if (job != null){ 
                GameObject textDisplayGo = Instantiate(UI.instance.JobDisplay, jobsPanel.transform);
                textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = job.name + ": " + (getJobCount(job)).ToString();
                textDisplayGo.name = "JobCount_" + job.name;
            }
        }
    }
    void updateJobCount(Job job){
        if (job != null){
            Transform textDisplayTransform = jobsPanel.transform.Find("JobCount_" + job.name);
            textDisplayTransform.gameObject.GetComponent<TMPro.TextMeshProUGUI>().text = job.name + ": " + (getJobCount(job)).ToString();
        }
    }
    int getJobCount(Job job){
        if (jobCounts.ContainsKey(job)){
            return jobCounts[job];
        } else {return 0;}
    }
    int getJobCount(string jobstr){
        return getJobCount(Db.getJobByName(jobstr));
    }

    public void OnClickJobAssignment(string jobstr, string buttontype){
        switch (buttontype){
            case "Add":
                AddJob(jobstr, 1);
                break;
            case "Subtract":
                AddJob(jobstr, -1);
                break;
            case "Zero":
                AddJob(jobstr, -getJobCount(jobstr));
                break;
        }
    }



}
