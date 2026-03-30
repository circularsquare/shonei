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

// ResearchSystem tracks per-node research progress.
//
// Each research node has a progress value (0 → 2×cost).
// Scientists contribute to the settlement's active research project.
// Progress decays over time — keeping scientists working is required to maintain unlocks.
//
// Unlock:  progress >= cost (and all prereqs are currently unlocked)
// Forget:  progress < 0.8 × cost (locks the research again)
public class ResearchSystem : MonoBehaviour {
    public static ResearchSystem instance { get; protected set; }

    const float DecayRate     = 0.02f;  // progress lost per tick (all nodes with any progress)
    const float ScientistRate = 0.1f;   // progress gained per workefficiency of a scientist each tick

    public Dictionary<int, float> progress       = new Dictionary<int, float>();
    public int                    activeResearchId = -1;

    public List<ResearchNodeData>            nodes       = new List<ResearchNodeData>();
    public Dictionary<int, ResearchNodeData> nodeById    = new Dictionary<int, ResearchNodeData>();
    public HashSet<int>                      unlockedIds = new HashSet<int>();

    // Maintain system: nodes the player wants scientists to keep above the unlock threshold.
    // Scientists prioritise maintained nodes that have fallen below cost before working on activeResearchId.
    public HashSet<int>            maintainIds       = new HashSet<int>();
    // Exclusive claims: non-active maintained nodes currently being worked on (nodeId → scientist).
    // The active research is never exclusively claimed — multiple scientists may maintain it.
    Dictionary<int, Animal>        maintenanceClaims = new Dictionary<int, Animal>();

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
            progress[node.id] = 0f;
        }
    }

    // Called from World.Update every second.
    public void TickUpdate() {
        foreach (var node in nodes) {
            if (progress.TryGetValue(node.id, out float p) && p > 0f)
                progress[node.id] = Mathf.Max(0f, p - DecayRate);
        }
        CheckTransitions();
    }

    public void CheckTransitions() {
        foreach (var node in nodes) {
            float p       = GetProgress(node.id);
            bool unlocked = IsUnlocked(node.id);
            if (!unlocked && p >= node.cost && PrereqsMet(node)) {
                unlockedIds.Add(node.id);
                ApplyEffect(node);
            } else if (unlocked && p < node.cost * 0.8f) {
                unlockedIds.Remove(node.id);
                RevertEffect(node);
            }
        }
    }

    public float GetProgress(int id) {
        progress.TryGetValue(id, out float p);
        return p;
    }

    public float GetCap(ResearchNodeData node) => node.cost * 2f;

    public bool IsUnlocked(int id) => unlockedIds.Contains(id);

    public bool PrereqsMet(ResearchNodeData node) {
        foreach (int prereq in node.prereqs)
            if (!IsUnlocked(prereq)) return false;
        return true;
    }

    // Returns true if this research can be set as the active project.
    // Prereqs must be unlocked; no restriction on whether the node itself is unlocked
    // (already-known research can still be reinforced to prevent forgetting).
    public bool CanSetActive(ResearchNodeData node) => PrereqsMet(node);

    public void SetActiveResearch(int id) {
        bool turningOn = activeResearchId != id;
        activeResearchId = turningOn ? id : -1;
        if (turningOn) SetMaintain(id, true);
    }

    // ── Maintain API ──────────────────────────────────────────────────

    public void ToggleMaintain(int id) {
        if (maintainIds.Contains(id)) maintainIds.Remove(id);
        else maintainIds.Add(id);
    }

    public void SetMaintain(int id, bool state) {
        if (state) maintainIds.Add(id);
        else maintainIds.Remove(id);
    }

    public bool IsMaintained(int id) => maintainIds.Contains(id);

    // Called when a scientist picks up a ResearchTask.
    // Returns the node ID the scientist should work on, or -1 for normal active research.
    //
    // Priority: (1) active research if it is maintained and below cost — no exclusive claim,
    //               multiple scientists may maintain it.
    //           (2) first non-active maintained node below cost that isn't already claimed.
    //           (3) -1 → fall through to activeResearchId in the caller.
    public int ClaimMaintenanceTarget(Animal scientist) {
        // (1) Active research maintenance — highest priority, no exclusive claim.
        if (activeResearchId >= 0
                && maintainIds.Contains(activeResearchId)
                && nodeById.TryGetValue(activeResearchId, out var activeNode)
                && GetProgress(activeResearchId) < activeNode.cost
                && PrereqsMet(activeNode)) {
            return activeResearchId;
        }

        // (2) Other maintained nodes — one scientist per node.
        foreach (int id in maintainIds) {
            if (id == activeResearchId) continue;
            if (!nodeById.TryGetValue(id, out var node)) continue;
            if (GetProgress(id) >= node.cost) continue;
            if (!PrereqsMet(node)) continue;
            if (maintenanceClaims.ContainsKey(id)) continue;
            maintenanceClaims[id] = scientist;
            return id;
        }

        return -1;
    }

    public void ReleaseMaintenanceClaim(int nodeId) {
        maintenanceClaims.Remove(nodeId);
    }

    // ── Scientist progress ──────────────────────────────────────────

    // Called from AnimalStateManager each research tick with the scientist's work efficiency.
    // targetId comes from ResearchTask.maintenanceTargetId; -1 means use activeResearchId.
    public void AddScientistProgress(float workEfficiency, int targetId) {
        int id = targetId >= 0 ? targetId : activeResearchId;
        if (id < 0) return;
        if (!nodeById.TryGetValue(id, out var node)) return;
        float gained = workEfficiency * ScientistRate * ModifierSystem.instance.GetResearchMultiplier();
        progress[id] = Mathf.Min(GetProgress(id) + gained, GetCap(node));
        CheckTransitions();
    }

    // Called from AnimalStateManager when a recipe cycle completes (passive skill gain).
    public void AddPassiveProgress(string researchName, float amount) {
        foreach (var node in nodes) {
            if (node.name == researchName) {
                progress[node.id] = Mathf.Min(GetProgress(node.id) + amount, GetCap(node));
                CheckTransitions();
                return;
            }
        }
    }

    // Applies the gameplay effect of a research node (called on unlock).
    public void ApplyEffect(ResearchNodeData node) {
        switch (node.type) {
            case "building":
                BuildPanel.instance?.UnlockBuilding(node.unlocks);
                break;
            case "misc":
                // research_efficiency is computed on demand by ModifierSystem.GetResearchMultiplier()
                break;
        }
    }

    // Reverts the gameplay effect of a research node (called on forget).
    public void RevertEffect(ResearchNodeData node) {
        switch (node.type) {
            case "building":
                BuildPanel.instance?.LockBuilding(node.unlocks);
                break;
            case "misc":
                // research_efficiency is computed on demand by ModifierSystem.GetResearchMultiplier()
                break;
        }
    }

    // Debug: fully unlock every node.
    public void UnlockAll() {
        foreach (var node in nodes) {
            progress[node.id] = GetCap(node);
            if (!IsUnlocked(node.id)) {
                unlockedIds.Add(node.id);
                ApplyEffect(node);
            }
        }
    }

    // Returns how many currently-unlocked nodes have the given unlocks value.
    public int CountUnlocks(string unlocksValue) {
        int count = 0;
        foreach (int id in unlockedIds)
            if (nodeById.TryGetValue(id, out var node) && node.unlocks == unlocksValue)
                count++;
        return count;
    }

    // Re-apply effects for all already-unlocked nodes (called after save load).
    public void ReapplyAllEffects() {
        foreach (int id in unlockedIds)
            if (nodeById.TryGetValue(id, out var node))
                ApplyEffect(node);
    }

    // Resets all research state to blank defaults (called by SaveSystem.ResetSystemState).
    // Reverts node effects (e.g. building unlocks) before clearing, so the build panel stays consistent.
    public void ResetAll() {
        foreach (int id in unlockedIds)
            if (nodeById.TryGetValue(id, out var node))
                RevertEffect(node);
        unlockedIds.Clear();
        foreach (var key in new List<int>(progress.Keys)) progress[key] = 0f;
        activeResearchId = -1;
        maintainIds.Clear();
        maintenanceClaims.Clear();
        CheckTransitions();
    }
}
