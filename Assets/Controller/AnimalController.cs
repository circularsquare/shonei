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

    private World world;
    public Dictionary<Job, int> jobCounts;

    // the purpose of this module right now is just to keep track of how many of each job there are
    // for display purposes.
    // actual animal updates are still in world.cs.

    void Start()
    {    
        if (instance != null) {
            Debug.LogError("there should only be one ani controller");}
        instance = this;   
        jobCounts = new Dictionary<Job, int>();
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
            if (ani != null){
                ani.RegisterCbAnimalChanged(OnAnimalChanged);
            }
        }

        panelJobs = UI.instance.transform.Find("PanelJobs").gameObject;
        // need to modify the below to look at the jobs enum rather than the Db.
        foreach(Job job in Db.jobs){
            if (job != null){ // need to add anotehr condintion for if there are no animals withthis job
                GameObject textDisplayGo = Instantiate(UI.instance.JobDisplay, panelJobs.transform);
                textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = job.name + ": " + (getJobCount(job)).ToString();
                textDisplayGo.name = "JobCount_" + job.name;
            }
        }
    }

    // this maybe probably be an interface or something
    // updates the number of mice with this job in the ui
    void updateJobCount(Job job){
        if (job != null){
            Transform textDisplayTransform = panelJobs.transform.Find("JobCount_" + job.name);
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

    public void OnAnimalChanged(Animal ani, Job oldJob) {

        // its pretty stupid to have this here rather than with the job change function.
        // maybe move it all except the updateJobCounts.
        // regardless it definitely shouldnt be in a OnChanged function cuz i think thats why its getting double called.
        Job newJob = ani.job;
        Debug.Log(newJob.name);
        Debug.Log(oldJob.name);
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

        //animalCount_ui.GetComponent<TextMeshProUGUI>().text = Db.itemById[1].iName + ": " + "5";
    }

    // handle job assignment button clicks
    public void OnClickJobAssignment(string jobstr, string buttontype){
        switch (buttontype){
            case "Add":
                world.AddJob(jobstr, 1);
                break;
            case "Subtract":
                world.AddJob(jobstr, -1);
                break;
            case "Zero":
                world.AddJob(jobstr, -getJobCount(jobstr));
                break;
        }

    }


}
