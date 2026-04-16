using System;
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
//   type   = "building", "recipe", or "job"
//   target = building name | recipe id (as string) | job name
public class UnlockEntry {
    public string type;
    public string target;
}

// ResearchSystem tracks per-node research progress.
//
// Each research node has a progress value (0 → 2×cost).
// Players toggle "study" on techs they want scientists to work on.
// Scientists prioritise studied techs below cost (oldest-unlocked first via LIFO),
// then reinforce above-cost techs (lowest % first). Progress decays over time —
// keeping scientists working is required to maintain unlocks.
//
// Unlock:  progress >= cost (and all prereqs are currently unlocked)
// Forget:  progress < 0.75 × cost (locks the research again, fires OnTechForgotten)
public class ResearchSystem : MonoBehaviour {
    public static ResearchSystem instance { get; protected set; }

    const float DecayRate        = 0.01f;  // progress lost per tick (all nodes with any progress)
    const float ScientistRate    = 0.05f;  // progress gained per workefficiency of a scientist each tick
    const float ConstructionGain = 1f;     // flat progress granted when a gated building is constructed

    public Dictionary<int, float> progress = new Dictionary<int, float>();

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

    // Reverse index: buildingName → technology node id that gates it.
    // Built once at load. Used by AddConstructionProgress to route a flat gain to
    // the right tech each time a gated building is constructed.
    Dictionary<string, int>                  buildingToTechNode = new Dictionary<string, int>();

    // Study system: techs the player wants scientists to research and maintain.
    // Scientists prioritise studied techs below cost (oldest-unlocked first), then
    // reinforce above-cost techs (lowest % first).
    public HashSet<int>            studiedIds        = new HashSet<int>();

    // Monotonic counter recording when each tech was last unlocked. Lower = older = higher
    // maintenance priority. Techs that have never been unlocked have no entry (treated as
    // int.MaxValue — lowest priority). Updated in CheckTransitions on unlock; re-stamped
    // if a forgotten tech is re-unlocked.
    public Dictionary<int, int>    unlockTimestamps  = new Dictionary<int, int>();
    public int                     unlockCounter;

    public static event Action<ResearchNodeData> OnTechForgotten;

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
        BuildBuildingLockIndex();
    }

    // Walks all node unlocks and records entries matching unlockType into the reverse index.
    // A target gated by multiple techs keeps the last one written, and logs an error so the
    // data-authoring mistake is caught. `parse` returns (false, _) to skip + log (e.g. recipe
    // targets must parse as int); returning (true, key) inserts at `key`.
    // Job validation against Db is deferred to ValidateJobUnlocks() because Db may not be
    // loaded when this runs.
    void BuildLockIndex<TKey>(string unlockType, Dictionary<TKey, int> index, Func<string, string, (bool ok, TKey key)> parse) {
        index.Clear();
        foreach (var node in nodes) {
            if (node.unlocks == null) continue;
            foreach (var e in node.unlocks) {
                if (e == null || e.type != unlockType) continue;
                var (ok, key) = parse(e.target, node.name);
                if (!ok) continue;
                if (index.ContainsKey(key))
                    Debug.LogError($"{unlockType} '{e.target}' is gated by multiple techs (last: '{node.name}') — using last.");
                index[key] = node.id;
            }
        }
    }

    void BuildRecipeLockIndex() {
        BuildLockIndex<int>("recipe", recipeToTechNode, (target, nodeName) => {
            if (!int.TryParse(target, out int rid)) {
                Debug.LogError($"Tech '{nodeName}' recipe unlock has non-integer target '{target}'");
                return (false, 0);
            }
            return (true, rid);
        });
    }

    void BuildJobLockIndex()      { BuildLockIndex<string>("job",      jobToTechNode,      (target, _) => (true, target)); }
    void BuildBuildingLockIndex() { BuildLockIndex<string>("building", buildingToTechNode, (target, _) => (true, target)); }

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
                unlockTimestamps[node.id] = ++unlockCounter;
                ApplyEffect(node);
            } else if (unlocked && p < node.cost * 0.75f) {
                unlockedIds.Remove(node.id);
                RevertEffect(node);
                Debug.Log($"Technology forgotten: {node.name}");
                OnTechForgotten?.Invoke(node);
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

    // Returns true if this tech can be studied (prereqs must be met).
    public bool CanStudy(ResearchNodeData node) => PrereqsMet(node);

    // ── Study API ─────────────────────────────────────────────────────

    public void ToggleStudy(int id) {
        if (studiedIds.Contains(id)) studiedIds.Remove(id);
        else studiedIds.Add(id);
    }

    public void SetStudy(int id, bool state) {
        if (state) studiedIds.Add(id);
        else studiedIds.Remove(id);
    }

    public bool IsStudied(int id) => studiedIds.Contains(id);

    // True if any animal is currently running a ResearchTask targeting this tech.
    // Used by ResearchPanel to show the cost text in green only while a scientist is
    // actively contributing — studied-but-no-scientist should not look the same as
    // studied-and-being-worked.
    public bool IsActivelyResearched(int id) {
        var ac = AnimalController.instance;
        if (ac == null || ac.animals == null) return false;
        foreach (var a in ac.animals) {
            if (a?.task is ResearchTask rt && rt.studyTargetId == id) return true;
        }
        return false;
    }

    // Called when a scientist picks up a ResearchTask.
    // Returns the node ID the scientist should work on, or -1 if nothing to do.
    //
    // Priority: (1) Studied techs below cost (need maintenance/research) — oldest-unlocked first.
    //               Never-unlocked techs are treated as newest (lowest priority).
    //           (2) Studied techs above cost but below 2×cost — lowest progress % first
    //               (spread reinforcement evenly).
    public int PickStudyTarget() {
        int bestBelow = -1;
        int bestBelowStamp = int.MaxValue;

        int bestAbove = -1;
        float bestAboveRatio = float.MaxValue;

        foreach (int id in studiedIds) {
            if (!nodeById.TryGetValue(id, out var node)) continue;
            if (!PrereqsMet(node)) continue;
            float p   = GetProgress(id);
            float cap = GetCap(node);

            if (p < node.cost) {
                // Below unlock threshold — prioritise oldest-unlocked.
                int stamp = unlockTimestamps.TryGetValue(id, out int s) ? s : int.MaxValue;
                if (stamp < bestBelowStamp) {
                    bestBelow = id;
                    bestBelowStamp = stamp;
                }
            } else if (p < cap) {
                // Above cost, below 2×cost — reinforce lowest % first.
                float ratio = p / cap;
                if (ratio < bestAboveRatio) {
                    bestAbove = id;
                    bestAboveRatio = ratio;
                }
            }
        }

        return bestBelow >= 0 ? bestBelow : bestAbove;
    }

    // ── Scientist progress ──────────────────────────────────────────

    // Called from AnimalStateManager each research tick with the scientist's work efficiency.
    // targetId comes from ResearchTask.studyTargetId; -1 means nothing to do.
    public void AddScientistProgress(float workEfficiency, int targetId) {
        if (targetId < 0) return;
        if (!nodeById.TryGetValue(targetId, out var node)) return;
        float gained = workEfficiency * ScientistRate;
        progress[targetId] = Mathf.Min(GetProgress(targetId) + gained, GetCap(node));
        CheckTransitions();
    }

    // Called from Blueprint.Complete each time a building finishes construction (gameplay path only,
    // not load/worldgen). Grants ConstructionGain × scale to the tech that gates the building.
    // Passive gain is maintain-only: caps at 2×cost and cannot unlock a locked tech — which is fine,
    // because a locked building cannot be constructed in the first place.
    //
    // `scale` lets callers grant a fraction of the full per-build gain. Used by MaintenanceTask
    // so a full 0→1 repair matches a fresh build, and a partial repair scales proportionally.
    public void AddConstructionProgress(string buildingName, float scale = 1f) {
        if (!buildingToTechNode.TryGetValue(buildingName, out int id)) return;
        if (!nodeById.TryGetValue(id, out var node)) return;
        progress[id] = Mathf.Min(GetProgress(id) + ConstructionGain * scale, GetCap(node));
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
                unlockTimestamps[node.id] = ++unlockCounter;
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
        studiedIds.Clear();
        unlockTimestamps.Clear();
        unlockCounter = 0;
        CheckTransitions();
    }
}
