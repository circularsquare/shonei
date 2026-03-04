using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

// this class keeps track of all the animals and adds animals and such
public class AnimalController : MonoBehaviour{
    public static AnimalController instance {get; protected set;}
    public Animal[] animals;
    public int na = 0;
    private int maxna = 1000;
    public GameObject jobsPanel;
    public GameObject happinessPanel;

    private World world;
    public Dictionary<Job, int> jobCounts;
    private TMPro.TextMeshProUGUI happinessDisplay;
    private int colonyTickCounter = 0;

    public float avgHappiness = 0f;
    public int totalHousingCapacity = 0;
    public int populationCapacity = 0;


    void Awake() {
        if (instance != null) {
            Debug.LogError("there should only be one ani controller");}
        instance = this;

        animals = new Animal[maxna];
        jobCounts = new Dictionary<Job, int>();
        
    }
    void Start() {
        jobCounts.Add(Db.jobs[0], 0);
    }


    public void TickUpdate(){ // called on a timer from World.cs
        if (world == null){
            world = WorldController.instance.world;
            AddJobCounts();  // this needs to run AFTER world has already been populated!
        }
        for (int a = 0; a < na; a++){
            animals[a].TickUpdate();
        }
        colonyTickCounter++;
        if (colonyTickCounter % 10 == 0) UpdateColonyStats();
    }

    public Animal AddAnimal(float x = 10, float y = 4){
        GameObject animalPrefab = Resources.Load<GameObject>("Prefabs/Animal");
        GameObject go = GameObject.Instantiate(animalPrefab, new Vector3(x, y, 0), Quaternion.identity);
        Animal animal = go.GetComponent<Animal>(); // already made in prefab!
        animal.x = x;
        animal.y = y;
        animal.id = na;
        animal.RegisterCbAnimalChanged(OnAnimalChanged);
        animal.transform.SetParent(transform);
        animals[na] = animal;
        jobCounts[Db.jobs[0]] += 1;
        na += 1;
        UpdateJobCount(Db.jobs[0]);
        return animal;
    }

    public void LoadAnimal(AnimalSaveData asd) {
        Animal animal = AddAnimal(asd.x, asd.y); // adds +1 to "none" job count
        animal.pendingSaveData = asd; // Animal.Start() (next frame) will apply name/stats/inv/job

        // Fix job counts now: move from "none" to saved job
        Job savedJob = Db.GetJobByName(asd.jobName);
        if (savedJob != null && savedJob.id != 0) {
            jobCounts[Db.jobs[0]] -= 1;
            if (!jobCounts.ContainsKey(savedJob)) jobCounts[savedJob] = 0;
            jobCounts[savedJob] += 1;
            UpdateJobCount(Db.jobs[0]);
            UpdateJobCount(savedJob);
        }
    }
    public void Load() {
        for (int a = 0; a < na; a++) animals[a].SlowUpdate(); // init happiness immediately on load
        UpdateColonyStats();
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
        UpdateJobCount(oldJob);
        UpdateJobCount(newJob);
    }

    // this maybe probably be an interface or something (shared code w inv controller)
    // updates the number of mice with this job in the ui

    void AddJobCounts(){
        foreach(Job job in Db.jobs){
            if (job != null){
                GameObject textDisplayGo = Instantiate(UI.instance.JobDisplay, jobsPanel.transform);
                textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = job.name + ": " + (GetJobCount(job)).ToString();
                textDisplayGo.name = "JobCount_" + job.name;
            }
        }
    }
    void UpdateJobCount(Job job){
        if (job != null && jobsPanel != null){
            Transform textDisplayTransform = jobsPanel.transform.Find("JobCount_" + job.name);
            if (textDisplayTransform != null){
                textDisplayTransform.gameObject.GetComponent<TMPro.TextMeshProUGUI>().text = job.name + ": " + (GetJobCount(job)).ToString();
            }
        }
    }
    void UpdateColonyStats(){
        if (na == 0) return;
        avgHappiness = 0f;
        for (int a = 0; a < na; a++) avgHappiness += animals[a].happiness.score;
        avgHappiness /= na;
        totalHousingCapacity = StructController.instance.TotalHousingCapacity();
        populationCapacity = Mathf.FloorToInt(avgHappiness * 2.5f);
        if (happinessDisplay == null)
            happinessDisplay = happinessPanel.GetComponent<TMPro.TextMeshProUGUI>();
        if (happinessDisplay != null)
            happinessDisplay.text = $"happiness: {avgHappiness:0.0}  pop: {na}/{populationCapacity}";
    }

    int GetJobCount(Job job){
        if (jobCounts.ContainsKey(job)){
            return jobCounts[job];
        } else {return 0;}
    }
    int GetJobCount(string jobstr){
        return GetJobCount(Db.GetJobByName(jobstr));
    }

    public void ResetJobCounts() {
        foreach (Job key in new List<Job>(jobCounts.Keys)) { jobCounts[key] = 0; }
        foreach (Job key in jobCounts.Keys) { UpdateJobCount(key); }
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
                AddJob(jobstr, -GetJobCount(jobstr));
                break;
        }
    }
}
