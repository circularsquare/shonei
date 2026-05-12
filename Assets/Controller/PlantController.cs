using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class PlantController : MonoBehaviour {
    public static PlantController instance { get; protected set; }
    private List<Plant> plants = new List<Plant>(); // list of plants
    public IReadOnlyList<Plant> Plants => plants;
    public int np = 0;

    // Subset of `plants` that use the blob-sway path (have a baked sway_meta).
    // Walked every Unity frame in Update() to translate per-blob child SRs
    // based on the current wind value. Kept separate so non-swaying plants
    // (grass, bamboo, crops) don't pay any per-frame cost.
    private readonly List<Plant> swayingPlants = new List<Plant>();
    // Last frame's "wind was effectively zero" state — when both this and the
    // current wind are below the epsilon, the entire sway loop is skipped
    // (everything is already at rest). The first zero frame still runs so
    // blobs snap to dx=0.
    private bool lastWindZero = true;

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
    public void Remove(Plant plant) {
        plants.Remove(plant);
        swayingPlants.Remove(plant);  // no-op if not registered
        np -= 1;
        WorkOrderManager.instance?.RemoveForTile(plant.tile);
    }

    // Plant calls this from its ctor if it has baked blob-sway data. The
    // controller's Update() walks this list each frame to drive per-blob
    // transform offsets. Unregistration happens via Remove() or the explicit
    // Unregister call below if a Plant transitions off the sway path.
    public void RegisterSwaying(Plant p) {
        swayingPlants.Add(p);
    }
    public void UnregisterSwaying(Plant p) {
        swayingPlants.Remove(p);
    }

    public void TickUpdate(){
        foreach (Plant plant in plants){
            plant.Grow(1);
        }
    }

    // Drives per-blob sway for every plant on the blob-sway path. Each blob
    // is one sin + transform write per frame (fractional displacement — no
    // change-detection skip). The "both frames zero → skip" guard at the
    // loop level makes calm weather cost effectively nothing.
    //
    // Wind is signed (positive = blowing right) and clamped to [-1, +1] here
    // so plant displacement maxes out at ±1 px regardless of how high
    // WeatherSystem's wind value pushes — keeps leaves from detaching when
    // wind values get set unusually high (manual debug, or future biome
    // multipliers).
    void Update() {
        if (swayingPlants.Count == 0) return;
        float rawWind = WeatherSystem.instance != null ? WeatherSystem.instance.wind : 0f;
        float wind    = Mathf.Clamp(rawWind, -1f, 1f);
        bool windZero = Mathf.Abs(wind) <= 0.001f;
        if (windZero && lastWindZero) return;
        lastWindZero = windZero;

        float t = Time.time;
        for (int i = 0; i < swayingPlants.Count; i++) {
            swayingPlants[i].UpdateBlobSway(t, wind);
        }
    }

}
