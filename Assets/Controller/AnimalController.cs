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
    public GameObject JobDisplay; 
    private World world;
    public Dictionary<Job, int> jobCounts;
    private Dictionary<Tile, int> tileOccupancy;
    private TMPro.TextMeshProUGUI happinessDisplay;
    private int colonyTickCounter = 0;
    private float tickAccumulator = 0f;
    private float prevTickAccumulator = 0f;
    private bool jobCountsInitialized = false;

    public float avgHappiness = 0f;
    public int totalHousingCapacity = 0;
    public int populationCapacity = 0;


    // FRAME 0 — before any Start(). Sets instance and allocates arrays.
    void Awake() {
        if (instance != null) {
            Debug.LogError("there should only be one ani controller");}
        instance = this;

        animals = new Animal[maxna];
        jobCounts = new Dictionary<Job, int>();
        tileOccupancy = new Dictionary<Tile, int>();

    }
    // FRAME 0 — populates jobCounts with jobs from Db (Db.Awake has already run).
    // Must finish before WorldController.Start() resumes in frame 1 and calls GenerateDefault().
    void Start() {
        jobCounts.Add(Db.jobs[0], 0);
    }


    // Staggered tick dispatch: each animal ticks exactly once per game-second,
    // but at a different point within that second based on its tickOffset.
    void Update() {
        if (na == 0) return;
        // Lazy init: job counts UI needs world to exist (frame 1+)
        if (!jobCountsInitialized && WorldController.instance?.world != null) {
            world = WorldController.instance.world;
            AddJobCounts();
            jobCountsInitialized = true;
        }
        prevTickAccumulator = tickAccumulator;
        tickAccumulator += Time.deltaTime;
        for (int a = 0; a < na; a++) {
            float off = animals[a].tickOffset;
            // Boundary crossing: has floor(t - offset) increased?
            if (Mathf.Floor(prevTickAccumulator - off) < Mathf.Floor(tickAccumulator - off))
                animals[a].TickUpdate();
        }
        // Colony-wide bookkeeping: once per full second boundary
        if (Mathf.Floor(prevTickAccumulator) < Mathf.Floor(tickAccumulator)) {
            colonyTickCounter++;
            if (colonyTickCounter % 10 == 0) UpdateColonyStats();
        }
    }

    public HashSet<string> UsedNames() {
        HashSet<string> names = new HashSet<string>();
        for (int i = 0; i < na; i++)
            if (animals[i] != null && !string.IsNullOrEmpty(animals[i].aName))
                names.Add(animals[i].aName);
        return names;
    }

    private int nextId = 0; // monotonic ID counter (not tied to array index)

    public Animal AddAnimal(float x = 10, float y = 4){
        GameObject animalPrefab = Resources.Load<GameObject>("Prefabs/Animal");
        GameObject go = GameObject.Instantiate(animalPrefab, new Vector3(x, y, -0.001f * nextId), Quaternion.identity);
        Animal animal = go.GetComponent<Animal>(); // already made in prefab!
        animal.x = x;
        animal.y = y;
        animal.id = nextId++;
        animal.RegisterCbAnimalChanged(OnAnimalChanged);
        animal.transform.SetParent(transform);
        // Don't add to animals[] yet — Animal.Start() calls RegisterReady()
        // once fully initialized, preventing ticks on half-constructed animals.
        jobCounts[Db.jobs[0]] += 1;
        UpdateJobCount(Db.jobs[0]);
        return animal;
    }

    /// <summary>
    /// Called from Animal.Start() once the animal is fully initialized.
    /// Adds it to the tickable animals array.
    /// </summary>
    public void RegisterReady(Animal animal) {
        // Golden ratio spread gives excellent distribution for any animal count
        animal.tickOffset = (animal.id * 0.618034f) % 1f;
        animals[na] = animal;
        na += 1;
    }

    public void LoadAnimal(AnimalSaveData asd) {
        Animal animal = AddAnimal(asd.x, asd.y); // adds +1 to "none" job count
        SnapToStandableBelow(animal);             // fix position if saved on non-standable tile (e.g. stairs)
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

    // Snaps an animal down to the nearest standable tile below their current position.
    // Called after LoadAnimal to handle cases where a mouse was saved on a non-standable tile (e.g. stairs).
    private void SnapToStandableBelow(Animal animal) {
        int ax = Mathf.RoundToInt(animal.x);
        int ay = Mathf.RoundToInt(animal.y);
        Tile t = World.instance.GetTileAt(ax, ay);
        if (t != null && t.node.standable) return; // already on a valid tile

        for (int checkY = ay - 1; checkY >= 0; checkY--) {
            Tile below = World.instance.GetTileAt(ax, checkY);
            if (below == null) break;
            if (below.node.standable) {
                animal.y = checkY;
                animal.gameObject.transform.position = new Vector3(animal.x, animal.y, 0);
                return;
            }
        }
        Debug.LogError($"SnapToStandableBelow: no standable tile found below ({ax},{ay}) for loaded animal");
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

    void AddJobCounts(){
        foreach(Job job in Db.jobs){
            if (job == null) continue;
            if (!IsJobVisible(job)) continue;
            AddJobRow(job);
        }
    }

    // True if this job should appear in the jobs panel right now.
    // Unlocked by default unless the job is flagged defaultLocked AND its gating
    // tech is not currently unlocked.
    bool IsJobVisible(Job job){
        if (!job.defaultLocked) return true;
        ResearchSystem rs = ResearchSystem.instance;
        return rs != null && rs.IsJobUnlocked(job.name);
    }

    void AddJobRow(Job job){
        if (jobsPanel == null || JobDisplay == null) return;
        if (jobsPanel.transform.Find("JobCount_" + job.name) != null) return; // idempotent
        GameObject textDisplayGo = Instantiate(JobDisplay, jobsPanel.transform);
        textDisplayGo.GetComponent<TMPro.TextMeshProUGUI>().text = job.name + ": " + (GetJobCount(job)).ToString();
        textDisplayGo.name = "JobCount_" + job.name;
    }

    // Called from ResearchSystem.ApplyEffect when a tech unlocks a job.
    // Idempotent: no-op if the row already exists or the panel hasn't been built yet
    // (AddJobCounts will pick it up once it runs, since IsJobVisible queries research state).
    public void UnlockJob(string jobName){
        Job job = Db.GetJobByName(jobName);
        if (job == null) { Debug.LogWarning($"UnlockJob: unknown job '{jobName}'"); return; }
        if (!jobCountsInitialized) return;
        AddJobRow(job);
    }

    // Called from ResearchSystem.RevertEffect when a tech is forgotten.
    // Reassigns all animals working this job back to "none" (so the tech decay
    // automatically frees them), then removes the row from the panel.
    public void LockJob(string jobName){
        Job job = Db.GetJobByName(jobName);
        if (job == null) { Debug.LogWarning($"LockJob: unknown job '{jobName}'"); return; }
        for (int a = 0; a < na; a++){
            if (animals[a] != null && animals[a].job != null && animals[a].job.name == jobName)
                animals[a].SetJob("none");
        }
        if (jobsPanel == null) return;
        Transform row = jobsPanel.transform.Find("JobCount_" + jobName);
        if (row != null) Destroy(row.gameObject);
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

    public bool IsAnimalOnTile(Tile tile) {
        for (int i = 0; i < na; i++) {
            if (animals[i].TileHere() == tile) return true;
        }
        return false;
    }

    // True if any animal other than `exclude` is standing on `tile`.
    public bool AnyOtherAnimalOnTile(Tile tile, Animal exclude) {
        for (int i = 0; i < na; i++) {
            if (animals[i] != exclude && animals[i].TileHere() == tile) return true;
        }
        return false;
    }

    // ── Tile occupancy tracking (O(1) crowding queries for movement speed) ───
    public void RegisterAnimalOnTile(Tile t) {
        if (t == null) return;
        tileOccupancy.TryGetValue(t, out int count);
        tileOccupancy[t] = count + 1;
    }
    public void UnregisterAnimalFromTile(Tile t) {
        if (t == null) return;
        if (tileOccupancy.TryGetValue(t, out int count)) {
            if (count <= 1) tileOccupancy.Remove(t);
            else tileOccupancy[t] = count - 1;
        }
    }
    public bool HasMultipleAnimalsOnTile(Tile t) {
        return t != null && tileOccupancy.TryGetValue(t, out int count) && count > 1;
    }
    public void ClearTileOccupancy() { tileOccupancy.Clear(); }
    public void ResetTickAccumulator() { tickAccumulator = 0f; prevTickAccumulator = 0f; }

    // Returns the nearest truly idle animal (no task, Idle state) within `radius` tiles, or null.
    public Animal FindIdleAnimalNear(Animal exclude, int radius) {
        Animal best = null;
        float bestDist = float.MaxValue;
        float r2 = radius * radius;
        for (int i = 0; i < na; i++) {
            Animal a = animals[i];
            if (a == exclude) continue;
            if (a.state != Animal.AnimalState.Idle) continue;
            if (a.task != null) continue;
            if (a.happiness.GetSatisfaction("social") > 4.0f) continue; // too socialized to chat
            float dx = a.x - exclude.x;
            float dy = a.y - exclude.y;
            float dist = dx * dx + dy * dy;
            if (dist < bestDist && dist <= r2) {
                best = a;
                bestDist = dist;
            }
        }
        return best;
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
