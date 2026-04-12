using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

// Research node definition — loaded from researchDb.json.
// A single technology can grant multiple unlocks of mixed types (e.g. a tech that
// unlocks both a building and a recipe), so `unlocks` is an array of per-entry rows.
public class ResearchNodeData {
    public int           id;
    public string        name;
    public string        description;
    public int[]         prereqs;
    public float         cost;
    public UnlockEntry[] unlocks;
}

// One unlock granted by a technology.
//   type   = "building", "recipe", or "misc"
//   target = building name | recipe id (as string) | misc effect key
public class UnlockEntry {
    public string type;
    public string target;
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

    // Reverse index: recipeId → technology node id that gates it.
    // Built once at load. Recipes not present in the map are unlocked by default.
    Dictionary<int, int>                     recipeToTechNode = new Dictionary<int, int>();

    // Reverse index: jobName → technology node id that gates it.
    // Built once at load. Jobs not present in the map are unlocked by default
    // (unless they are still flagged defaultLocked, in which case they are permanently
    // locked — a data-authoring error worth catching).
    Dictionary<string, int>                  jobToTechNode    = new Dictionary<string, int>();

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

    // Validation that depends on Db (jobs, etc.) runs in Start so Awake ordering doesn't matter.
    void Start() {
        ValidateJobUnlocks();
    }

    void LoadNodes() {
        string path = Application.dataPath + "/Resources/researchDb.json";
        if (!File.Exists(path)) { Debug.LogWarning("researchDb.json not found"); return; }
        string json = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<ResearchNodeData[]>(json);
        foreach (var node in loaded) {
            if (node.prereqs == null) node.prereqs = new int[0];
            if (node.unlocks == null) node.unlocks = new UnlockEntry[0];
            nodes.Add(node);
            nodeById[node.id] = node;
            progress[node.id] = 0f;
        }
        BuildRecipeLockIndex();
        BuildJobLockIndex();
    }

    // Walks all node unlocks and records recipe-type entries into the reverse index.
    // A recipe gated by multiple techs would only remember the last one written — flagged here.
    void BuildRecipeLockIndex() {
        recipeToTechNode.Clear();
        foreach (var node in nodes) {
            if (node.unlocks == null) continue;
            foreach (var e in node.unlocks) {
                if (e == null || e.type != "recipe") continue;
                if (!int.TryParse(e.target, out int rid)) {
                    Debug.LogError($"Tech '{node.name}' recipe unlock has non-integer target '{e.target}'");
                    continue;
                }
                if (recipeToTechNode.ContainsKey(rid))
                    Debug.LogError($"Recipe {rid} is gated by multiple techs (last: '{node.name}') — using last.");
                recipeToTechNode[rid] = node.id;
            }
        }
    }

    // Walks all node unlocks and records job-type entries into the reverse index.
    // A job gated by multiple techs would only remember the last one written — flagged here.
    // Runs in Awake before Db is guaranteed loaded, so target-name validation is deferred
    // to ValidateJobUnlocks() which runs in Start.
    void BuildJobLockIndex() {
        jobToTechNode.Clear();
        foreach (var node in nodes) {
            if (node.unlocks == null) continue;
            foreach (var e in node.unlocks) {
                if (e == null || e.type != "job") continue;
                if (jobToTechNode.ContainsKey(e.target))
                    Debug.LogError($"Job '{e.target}' is gated by multiple techs (last: '{node.name}') — using last.");
                jobToTechNode[e.target] = node.id;
            }
        }
    }

    // Called from Start() — after Db.Awake has populated jobByName.
    // Flags typos (tech pointing at a non-existent job name) and orphans
    // (defaultLocked jobs with no gating tech).
    void ValidateJobUnlocks() {
        foreach (var kv in jobToTechNode) {
            if (Db.GetJobByName(kv.Key) == null) {
                if (nodeById.TryGetValue(kv.Value, out var node))
                    Debug.LogError($"Tech '{node.name}' job unlock has unknown target '{kv.Key}'");
            }
        }
        foreach (Job j in Db.jobs) {
            if (j == null || !j.defaultLocked) continue;
            if (!jobToTechNode.ContainsKey(j.name))
                Debug.LogError($"Job '{j.name}' is defaultLocked but no tech unlocks it — it will never appear in the jobs panel.");
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
        float gained = workEfficiency * ScientistRate;
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

    // True if the recipe is either ungated or its gating tech is currently unlocked.
    // Ungated recipes (no tech references them) are always considered unlocked.
    public bool IsRecipeUnlocked(int recipeId) {
        if (!recipeToTechNode.TryGetValue(recipeId, out int techNodeId)) return true;
        return unlockedIds.Contains(techNodeId);
    }

    // True if the job is unlocked: either no tech gates it (via an unlocks entry), or
    // its gating tech is currently unlocked. Used by AnimalController to decide whether
    // to show a job row in the jobs panel.
    public bool IsJobUnlocked(string jobName) {
        if (!jobToTechNode.TryGetValue(jobName, out int techNodeId)) return true;
        return unlockedIds.Contains(techNodeId);
    }

    // Applies the gameplay effect of a technology (called on unlock).
    // Recipe unlocks need no action here — animal recipe filters query IsRecipeUnlocked
    // live, and RecipePanel rebuilds on open so newly-unlocked recipes appear automatically.
    public void ApplyEffect(ResearchNodeData node) {
        if (node.unlocks == null) return;
        foreach (var e in node.unlocks) {
            if (e == null) continue;
            switch (e.type) {
                case "building":
                    BuildPanel.instance?.UnlockBuilding(e.target);
                    break;
                case "job":
                    AnimalController.instance?.UnlockJob(e.target);
                    break;
                case "recipe":
                case "misc":
                    break;
                default:
                    Debug.LogError($"Tech '{node.name}' has unknown unlock type '{e.type}'");
                    break;
            }
        }
    }

    // Reverts the gameplay effect of a technology (called on forget).
    public void RevertEffect(ResearchNodeData node) {
        if (node.unlocks == null) return;
        foreach (var e in node.unlocks) {
            if (e == null) continue;
            switch (e.type) {
                case "building":
                    BuildPanel.instance?.LockBuilding(e.target);
                    break;
                case "job":
                    AnimalController.instance?.LockJob(e.target);
                    break;
                case "recipe":
                case "misc":
                    break;
                default:
                    Debug.LogError($"Tech '{node.name}' has unknown unlock type '{e.type}'");
                    break;
            }
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
