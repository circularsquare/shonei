using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

// Research node definition — loaded from researchDb.json.
public class ResearchNodeData {
    public int    id;
    public string name;
    public string description;
    public string type;      // "building", "recipe", "misc"
    public int[]  prereqs;
    public float  cost;
    public string unlocks;  // building/recipe name for typed nodes
}

// ResearchSystem tracks available research points using a rolling-window approach:
//   - Every (ticksInDay / 12) ticks, sample the current scientist output.
//   - Keep the last 15 samples in a circular buffer.
//   - Available points = max of those 15 samples minus total already spent.
//   - This gives a stable value that reflects recent peak productivity without
//     swinging every time a mouse takes a break.
public class ResearchSystem : MonoBehaviour {
    public static ResearchSystem instance;

    const int HistorySize = 15;

    public float[] pointHistory  = new float[HistorySize];
    public int     historyIndex  = 0;
    public float   totalSpent    = 0f;
    public int     tickCounter   = 0;

    public float AvailablePoints {
        get {
            float peak = 0f;
            foreach (float v in pointHistory) if (v > peak) peak = v;
            return Mathf.Max(0f, peak - totalSpent);
        }
    }

    public List<ResearchNodeData>            nodes    = new List<ResearchNodeData>();
    public Dictionary<int, ResearchNodeData> nodeById = new Dictionary<int, ResearchNodeData>();
    public HashSet<int>                      unlockedIds = new HashSet<int>();

    // Runtime state modified by research effects.
    public float researchEfficiencyMultiplier = 1f;

    void Awake() {
        if (instance != null) { Debug.LogError("two ResearchSystems!"); }
        instance = this;
        LoadNodes();
    }

    void LoadNodes() {
        string path = Application.dataPath + "/Resources/researchDb.json";
        if (!File.Exists(path)) { Debug.LogWarning("researchDb.json not found"); return; }
        string json = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<ResearchNodeData[]>(json);
        foreach (var node in loaded) {
            if (node.prereqs == null) node.prereqs = new int[0];
            nodes.Add(node);
            nodeById[node.id] = node;
        }
    }

    // Called from World.Update every second.
    public void TickUpdate() {
        int interval = Mathf.Max(1, Db.ticksInDay / 12);
        tickCounter++;
        if (tickCounter >= interval) {
            tickCounter = 0;
            pointHistory[historyIndex] = GetCurrentCapacity();
            historyIndex = (historyIndex + 1) % HistorySize;
        }
    }

    // Current research output = working scientists * 10.
    float GetCurrentCapacity() {
        AnimalController ac = AnimalController.instance;
        if (ac == null) return 0f;
        int scientists = 0;
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a.job?.name == "scientist" && a.task is ResearchTask
                    && a.state == Animal.AnimalState.Working)
                scientists++;
        }
        return scientists * 10f * researchEfficiencyMultiplier;
    }

    public bool IsUnlocked(int id) => unlockedIds.Contains(id);

    public bool CanUnlock(ResearchNodeData node) {
        if (IsUnlocked(node.id)) return false;
        if (AvailablePoints < node.cost) return false;
        foreach (int prereq in node.prereqs)
            if (!IsUnlocked(prereq)) return false;
        return true;
    }

    public bool Unlock(ResearchNodeData node) {
        if (!CanUnlock(node)) return false;
        totalSpent += node.cost;
        unlockedIds.Add(node.id);
        ApplyEffect(node);
        return true;
    }

    // Applies the gameplay effect of a research node.
    // All research effects are handled here.
    public void ApplyEffect(ResearchNodeData node) {
        switch (node.type) {
            case "building":
                BuildPanel.instance?.UnlockBuilding(node.unlocks);
                break;
            case "misc":
                if (node.unlocks == "research_efficiency")
                    researchEfficiencyMultiplier *= 1.2f;
                break;
        }
    }

    // Re-apply effects for all already-unlocked nodes (called after save load).
    public void ReapplyAllEffects() {
        researchEfficiencyMultiplier = 1f;
        foreach (int id in unlockedIds)
            if (nodeById.TryGetValue(id, out var node))
                ApplyEffect(node);
    }
}
