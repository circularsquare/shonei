using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;
using TMPro;

// this class keeps track of all the animals and adds animals and such
public class AnimalController : MonoBehaviour{
    public static AnimalController instance {get; protected set;}
    public Animal[] animals;
    public int na = 0;
    private int maxna = 1000;
    public GameObject jobsPanel;
    public CollapsibleHeader jobsHeader; // optional; SaveSystem reads/writes open state via saveKey
    // Outer wood-framed container (the JobsScroll RectTransform). When the header collapses,
    // we resize this to just the header's height so the wood frame visually shrinks too.
    // Optional — leave unwired to skip the resize behaviour.
    public RectTransform jobsScrollRect;
    private float _jobsScrollExpandedHeight = -1f;
    // Top-bar readout TMP GameObject ("happiness X  pop n/cap"). Held to fetch its
    // TextMeshProUGUI; the click-to-open Button is happinessButton below.
    [FormerlySerializedAs("happinessPanel")] public GameObject happinessReadoutText;
    // Top-bar readout button — clicking it opens the full GlobalHappinessPanel.
    [SerializeField] UnityEngine.UI.Button happinessButton;
    public GameObject JobDisplay;
    private World world;
    public Dictionary<Job, int> jobCounts;
    private Dictionary<Tile, int> tileOccupancy;
    // Remembers the last mouse shown for each job name so clicking a row in
    // the jobs panel cycles through its mice instead of always landing on the
    // first one. Stale entries (mouse dead or changed job) are harmless —
    // they fall out of the matches list and we reset to the start.
    private Dictionary<string, Animal> lastShownForJob = new Dictionary<string, Animal>();
    private TMPro.TextMeshProUGUI happinessDisplay;
    private int colonyTickCounter = 0;
    private float tickAccumulator = 0f;
    private float prevTickAccumulator = 0f;
    private bool jobCountsInitialized = false;

    public float avgHappiness = 0f;
    public int totalHousingCapacity = 0;
    public int populationCapacity = 0;

    // Population cap when every mouse has every happiness need fully satisfied.
    // populationCapacity scales linearly: avgHappiness / Db.happinessMaxScore × MaxPopulationCap.
    // This decouples the cap from the score-cap composition so adding/removing happiness
    // needs no longer shifts the implicit ceiling.
    public const int MaxPopulationCap = 40;

    // Early-growth boost: the first EarlyBirthBoostBirths births (starting colony of 4 → 6) roll
    // at EarlyBirthBoostMultiplier× the normal chance, so a fresh colony gets moving faster.
    // `births` is the cumulative birth count this colony (persisted, reset on fresh world). It
    // does NOT decrease on death, so the boost is strictly the first two births — not "while pop < 6".
    public const int   EarlyBirthBoostBirths     = 2;
    public const float EarlyBirthBoostMultiplier = 2f;
    public int births = 0;

    // ── Colony food-storage stats (driven by UpdateColonyStats every 10 ticks) ──
    // daysOfFoodInStorage: total stored food in colony, expressed as days-of-hunger
    // per current mouse. 0 with no food, +infinity with no mice.
    // foodStorageHappinessBonus: 0..4, applied uniformly to every mouse's happiness
    // score by Happiness.SlowUpdate. Caps out at MaxFoodStorageBonus when daysOfFood
    // ≥ FoodStorageBonusFullDays — same threshold used by the birth-rate taper.
    public float daysOfFoodInStorage = 0f;
    public float foodStorageHappinessBonus = 0f;
    public const float MaxFoodStorageBonus = 4f;
    public const float FoodStorageBonusFullDays = 10f;

    // Mice are queued by AddAnimal but only counted in `na` once Animal.Start runs
    // RegisterReady (next frame). pendingAnimals bridges that gap so MaybeOfferRescue
    // doesn't mistake the registration lag — after a rescue wave, the initial worldgen
    // seed, a save load, or a birth — for an under-populated colony and spawn a
    // spurious second wave. Zeroed by ResetColonyState (WorldController.ClearWorld).
    private int pendingAnimals = 0;

    // Set once the colony-wiped "spawn newcomers?" prompt has been shown this session,
    // so we don't nag every frame the colony sits empty. Runtime-only (not saved) and
    // cleared by ResetColonyState, so reloading a save re-arms the prompt.
    private bool rescuePromptShown = false;

    // False during the startup/load window — the world grid (and World.instance) exists
    // several frames before the colony's mice are loaded/seeded, so na == 0 alone can't
    // tell "wiped out" from "not loaded yet". Set true in Load() (the post-load hook run
    // by every world-creation path) once na is finalized; cleared by ResetColonyState.
    private bool colonyReady = false;


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
        if (jobsHeader != null) jobsHeader.onToggled += OnHeaderToggled;
        // The whole top-bar readout opens the happiness breakdown. Wired in code (not the
        // inspector) so the static-instance Toggle() target can't drift.
        if (happinessButton != null)
            happinessButton.onClick.AddListener(() => {
                if (GlobalHappinessPanel.instance != null) GlobalHappinessPanel.instance.Toggle();
            });
    }

    // Mirrors InventoryController.OnHeaderToggled — shrinks the wood-framed scroll container
    // to just the header height when collapsed, restores designer-set height on expand.
    void OnHeaderToggled(bool open){
        if (jobsScrollRect == null) return;
        if (_jobsScrollExpandedHeight < 0)
            _jobsScrollExpandedHeight = jobsScrollRect.sizeDelta.y;
        var sd = jobsScrollRect.sizeDelta;
        sd.y = open ? _jobsScrollExpandedHeight : 22f;
        jobsScrollRect.sizeDelta = sd;
    }


    // Staggered tick dispatch: each animal ticks exactly once per game-second,
    // but at a different point within that second based on its tickOffset.
    void Update() {
        Tick(Time.deltaTime);
    }

    // Advances animal ticks by `dt` seconds. Production calls Tick(Time.deltaTime).
    // Tests / snapshot harness call with a fixed dt for deterministic stepping.
    public void Tick(float dt) {
        // No early-out on na == 0: the per-animal loop and RemoveDeadAnimals are
        // already no-ops when empty, and we MUST keep running so MaybeOfferRescue can
        // prompt to repopulate a fully wiped colony and UpdateColonyStats can clear the
        // stale top-bar readout.
        // Lazy init: job counts UI needs world to exist (frame 1+)
        if (!jobCountsInitialized && WorldController.instance?.world != null) {
            world = WorldController.instance.world;
            AddJobCounts();
            jobCountsInitialized = true;
        }
        prevTickAccumulator = tickAccumulator;
        tickAccumulator += dt;
        for (int a = 0; a < na; a++) {
            float off = animals[a].tickOffset;
            // Boundary crossing: has floor(t - offset) increased?
            if (Mathf.Floor(prevTickAccumulator - off) < Mathf.Floor(tickAccumulator - off))
                animals[a].TickUpdate();
        }
        // Remove any mice that starved to death this tick. Done here — after the
        // tick loop — so animals[] is never compacted mid-iteration.
        RemoveDeadAnimals();
        // If the colony just wiped out, offer the player a fresh wave (once per session).
        MaybeOfferRescue();
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
        pendingAnimals += 1; // cleared in RegisterReady once Animal.Start runs
        return animal;
    }

    // Called from Animal.Start() once the animal is fully initialized.
    // Adds it to the tickable animals array.
    public void RegisterReady(Animal animal) {
        if (pendingAnimals > 0) pendingAnimals -= 1;
        // Golden ratio spread gives excellent distribution for any animal count
        animal.tickOffset = (animal.id * 0.618034f) % 1f;
        animals[na] = animal;
        na += 1;
    }

    // Clears all per-world colony bookkeeping so a fresh world (gen / reset / load)
    // starts clean. Call AFTER WorldController.ClearWorld has destroyed the live
    // animals — this zeroes the count. Clearing colonyReady/rescuePromptShown here is
    // what re-arms the wipeout prompt on every reload.
    public void ResetColonyState() {
        na = 0;
        pendingAnimals = 0;
        rescuePromptShown = false;
        colonyReady = false;
    }

    // Compacts animals[] in place, removing any flagged pendingDeath (set by
    // Animal.TickUpdate on starvation). Each removed mouse is torn down via
    // HandleDeath. Called from Tick() after the per-animal loop so the array is
    // never mutated mid-iteration; cheap when nobody died — a single linear pass.
    private void RemoveDeadAnimals() {
        int write = 0;
        for (int read = 0; read < na; read++) {
            Animal a = animals[read];
            if (a != null && a.pendingDeath) {
                HandleDeath(a);
                continue; // drop from the compacted array
            }
            animals[write++] = a;
        }
        bool anyDied = write < na;
        for (int i = write; i < na; i++) animals[i] = null;
        na = write;
        // Refresh the top-bar readout the moment someone dies — otherwise the pop count
        // lags by up to 10 ticks (the UpdateColonyStats cadence), and a colony that just
        // emptied keeps showing its last non-zero population.
        if (anyDied) UpdateColonyStats();
    }

    // Tears down one mouse that has died. Order matters: drop its goods to the
    // floor and fix the job count BEFORE Destroy() nulls the animal's references.
    private void HandleDeath(Animal a) {
        EventFeed.instance?.Post($"<color=#ff4444>{a.aName} starved to death.</color>");
        a.DropInventoryToFloor();
        if (a.job != null && jobCounts.ContainsKey(a.job)) {
            jobCounts[a.job] -= 1;
            UpdateJobCount(a.job);
        }
        // Drop the InfoPanel selection if it was showing this mouse — its
        // GameObject is about to be destroyed.
        if (InfoPanel.instance != null && InfoPanel.instance.IsAnimalSelected(a))
            InfoPanel.instance.ShowInfo(null);
        a.Destroy();
    }

    // When the colony has been wiped out (every mouse starved), offer the player a
    // fresh wave rather than auto-spawning — they may want to watch the empty colony
    // or accept the loss. Shown once per session; reloading a save re-arms it (the
    // flag is runtime-only and ClearWorld resets it). On decline the popup just
    // closes and we never re-prompt this session.
    //
    // Gated on colonyReady (suppresses the whole startup/load window) and on
    // pendingAnimals == 0 (mice queued via AddAnimal but not yet registered), so only a
    // genuine post-load empty colony — wiped out in play, or a saved 0-mouse colony —
    // trips the prompt.
    private void MaybeOfferRescue() {
        if (!colonyReady || rescuePromptShown) return;
        if (na != 0 || pendingAnimals != 0) return;

        rescuePromptShown = true;
        ConfirmationPopup.Show("all mice gone. spawn 4 newcomers?", SpawnRescueWave);
    }

    // Spawns a fresh wave of 4 mice. Spawn site: any existing housing building, or —
    // failing that — the central spawn zone where mice originally arrive (near the
    // starter plants). The old left-edge fallback (x=1) was often cut off from the
    // rest of the colony by terrain, stranding the newcomers.
    private void SpawnRescueWave() {
        const int count = 4;
        float sx, sy;
        Building home = FindAnyHousing();
        if (home != null) {
            sx = home.x;
            sy = home.y;
        } else {
            int[] surf = World.instance?.surfaceY;
            int spawnX = (WorldGen.config.SpawnMinX + WorldGen.config.SpawnMaxX) / 2;
            if (surf == null || surf.Length <= spawnX) {
                Debug.LogError("SpawnRescueWave: surfaceY unavailable, skipping rescue");
                return;
            }
            sx = spawnX;
            sy = surf[spawnX];
        }
        for (int i = 0; i < count; i++) AddAnimal(sx, sy);
        EventFeed.instance?.Post($"<color=#66ff66>{count} mice arrived at the colony.</color>");
    }

    private Building FindAnyHousing() {
        StructController sc = StructController.instance;
        if (sc == null) return null;
        foreach (Structure s in sc.GetStructures()) {
            if (s is Building b && b.structType.isHousing) return b;
        }
        return null;
    }

    public void LoadAnimal(AnimalSaveData asd) {
        Animal animal = AddAnimal(asd.x, asd.y); // adds +1 to "none" job count
        // Settle the loaded mouse onto a real graph node. Save data carries only raw
        // (x, y), so a mouse saved mid-traversal (ladder / stairs / cliff / rope bridge)
        // comes back off-grid; snapping it onto the nearest standable node kills the
        // diagonal drift on its first move and the mid-air idle. Skips mice legitimately
        // inside a building's interior (non-standable dirt — they belong there).
        SnapOntoGraph(animal);
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
        colonyReady = true; // colony finalized — MaybeOfferRescue may now fire on a wipeout
    }

    // Settles a freshly-loaded animal onto the nearest standable graph node. Save data
    // carries only raw (x, y), so a mouse saved mid-traversal returns off-grid; snapping
    // it onto a node prevents the diagonal first-move drift and the mid-air idle.
    // Runs before Animal.Start() (a later frame), so animal.go is still null — we write
    // the transform directly rather than calling animal.SnapTo (which assumes go is set).
    private void SnapOntoGraph(Animal animal) {
        // Mice legitimately inside a building's hollow interior sit on non-standable
        // dirt (e.g. a burrow's preserved substrate) — they belong there, leave them.
        Tile here = World.instance.GetTileAt(animal.x, animal.y);
        if (here != null && here.interiorBuilding != null) return;

        Node n = World.instance.graph.FindNearestStandableNode(animal.x, animal.y);
        if (n != null) {
            animal.x = n.wx;
            animal.y = n.wy;
            animal.gameObject.transform.position = new Vector3(n.wx, n.wy, 0);
            return;
        }

        // Fallback: scan straight down for standable ground. Covers footing the graph's
        // chain sets don't enumerate (e.g. an elevator shaft).
        int ax = Mathf.RoundToInt(animal.x);
        int ay = Mathf.RoundToInt(animal.y);
        for (int checkY = ay - 1; checkY >= 0; checkY--) {
            Tile below = World.instance.GetTileAt(ax, checkY);
            if (below == null) break;
            if (below.node.standable) {
                animal.y = checkY;
                animal.gameObject.transform.position = new Vector3(animal.x, animal.y, 0);
                return;
            }
        }
        Debug.LogError($"SnapOntoGraph: no standable node found near ({ax},{ay}) for loaded animal");
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
        if (happinessDisplay == null && happinessReadoutText != null)
            happinessDisplay = happinessReadoutText.GetComponent<TMPro.TextMeshProUGUI>();
        // Empty colony: render a zeroed readout rather than leaving the last non-zero
        // population frozen on the top bar (the bug where a wiped colony showed "pop 4/3").
        if (na == 0) {
            avgHappiness = 0f;
            populationCapacity = 0;
            if (happinessDisplay != null) happinessDisplay.text = "pop 0";
            return;
        }
        avgHappiness = 0f;
        for (int a = 0; a < na; a++) avgHappiness += animals[a].happiness.score;
        avgHappiness /= na;
        totalHousingCapacity = StructController.instance.TotalHousingCapacity();

        // Recompute food-storage stats before pop-cap so any mice ticking SlowUpdate
        // on the same frame see the up-to-date colony bonus.
        daysOfFoodInStorage = ComputeDaysOfFoodInStorage();
        foodStorageHappinessBonus = Mathf.Clamp01(daysOfFoodInStorage / FoodStorageBonusFullDays) * MaxFoodStorageBonus;

        // Linear rescale from avgHappiness ∈ [0, happinessMaxScore] to [0, MaxPopulationCap].
        // Falls back to the legacy ×2.5 constant if Db hasn't finished loading (shouldn't
        // happen in practice — by here na > 0, so Db is loaded).
        int maxScore = Db.happinessMaxScore;
        populationCapacity = maxScore > 0
            ? Mathf.FloorToInt(avgHappiness / maxScore * MaxPopulationCap)
            : Mathf.FloorToInt(avgHappiness * 2.5f);
        if (happinessDisplay != null)
            happinessDisplay.text = $"happiness {avgHappiness:0.0}  pop {na}/{populationCapacity}";
    }

    // Days of food in colony storage, expressed per current mouse.
    // hungerRate × ticksInDay hunger units per mouse per day; each food's foodValue is
    // hunger restored per liang eaten (quantities are stored in fen — 100 fen = 1 liang).
    // Returns +Infinity when the colony is empty so callers don't have to special-case na == 0.
    public float ComputeDaysOfFoodInStorage() {
        if (na <= 0) return float.PositiveInfinity;
        GlobalInventory ginv = GlobalInventory.instance;
        if (ginv == null || Db.edibleItems == null) return 0f;
        float totalHunger = 0f;
        foreach (Item food in Db.edibleItems) {
            int qFen = ginv.Quantity(food);
            if (qFen <= 0) continue;
            totalHunger += (qFen / 100f) * food.foodValue;
        }
        // hungerRate is a per-Animal field (default 0.4); colony-wide stat uses the
        // default value rather than averaging across animals — mice all share the rate today.
        const float defaultHungerRate = 0.2f;
        float perMousePerDay = defaultHungerRate * World.ticksInDay; // 0.2 × 480 = 96 by default
        if (perMousePerDay <= 0f) return 0f;
        return totalHunger / (perMousePerDay * na);
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

    // Called from JobDisplay when the player clicks the label of a job row.
    // Finds all mice with that job and shows one in InfoPanel; successive
    // clicks cycle through the roster. No-op if no mice have this job.
    public void SelectAnimalWithJob(string jobName){
        List<Animal> matches = new List<Animal>();
        for (int a = 0; a < na; a++){
            Animal ani = animals[a];
            if (ani != null && ani.job != null && ani.job.name == jobName)
                matches.Add(ani);
        }
        if (matches.Count == 0) return;

        int nextIdx = 0;
        if (lastShownForJob.TryGetValue(jobName, out Animal last) && last != null){
            int prev = matches.IndexOf(last);
            if (prev >= 0) nextIdx = (prev + 1) % matches.Count;
        }
        Animal chosen = matches[nextIdx];
        lastShownForJob[jobName] = chosen;

        if (InfoPanel.instance == null){
            Debug.LogError("SelectAnimalWithJob: InfoPanel.instance is null");
            return;
        }
        InfoPanel.instance.ShowInfo(new List<Animal>{ chosen });

        // Jump the camera to the picked mouse once (no follow).
        MouseController.instance?.CenterCameraOn(chosen.x, chosen.y);
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
