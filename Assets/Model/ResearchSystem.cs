using System;
using System.Collections.Generic;
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

    const float DecayRate        = 0.004f; // progress lost per tick (all nodes with any progress)
    const float ScientistRate    = 0.02f;  // progress gained per workefficiency of a scientist each tick

    // Unlocked techs below MaintainThreshold × cost still count as "needs work" in PickStudyTarget,
    // so scientists top up a slightly-decayed unlock (e.g. 109%) before starting a brand-new tech.
    const float MaintainThreshold = 1.10f;
    const float ForgetThreshold   = 0.75f; // unlocked tech reverts to locked once progress falls below this × cost

    // Passive (non-scientist) research, both scaled by labour time so a faster / more skilled
    // worker contributes proportionally more — mirroring how a scientist's per-tick gain scales.
    //   Crafting: a baseline (eff 1.0) worker crafting a research recipe continuously earns 30%
    //             of a baseline scientist's rate. workload (seconds/cycle) cancels out, so the
    //             daily yield is the same for every research recipe regardless of its workload.
    //   Construction: research per tick of build labour — a 10-tick build grants 1.0.
    public const float PassiveCraftRate  = 0.3f * ScientistRate; // per second of craft labour (applied at the craft call site)
    const float ConstructionResearchRate = 0.05f;                // per tick of build labour

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
    public static event Action<ResearchNodeData> OnTechUnlocked;

    void Awake() {
        if (instance != null) { Debug.LogError("two ResearchSystems!"); }
        instance = this;
        LoadNodes();
    }

    // Index building is deferred to Start() because it depends on Db.Awake having populated
    // bookRecipeIdByTechId (runtime-generated scribe recipes). Awake() ordering between
    // MonoBehaviours is indeterminate, but every Awake runs before any Start — so by the time
    // we reach here, Db is guaranteed to be ready. Past bug: when ResearchSystem.Awake won the
    // race, InjectBookRecipeUnlocks saw an empty map and scribe recipes ended up ungated, letting
    // scribes write books for locked techs.
    void Start() {
        InjectBookRecipeUnlocks();
        BuildRecipeLockIndex();
        BuildJobLockIndex();
        BuildBuildingLockIndex();
        ValidateJobUnlocks();
    }

    void LoadNodes() {
        // Resources.Load works in both Editor and built player; the old
        // Application.dataPath + "/Resources/..." path silently breaks in builds.
        TextAsset ta = Resources.Load<TextAsset>("researchDb");
        if (ta == null) { Debug.LogError("researchDb.json not found in Resources/"); return; }
        var loaded = JsonConvert.DeserializeObject<ResearchNodeData[]>(ta.text);
        foreach (var node in loaded) {
            if (node.prereqs == null) node.prereqs = new int[0];
            if (node.unlocks == null) node.unlocks = new UnlockEntry[0];
            nodes.Add(node);
            nodeById[node.id] = node;
            progress[node.id] = 0f;
        }
    }

    // Db.GenerateBookRecipes runs during Db.Awake (before this method) and populates
    // bookRecipeIdByTechId with one scribe recipe per tech. Gate each of those recipes
    // behind its own tech by appending a recipe-type UnlockEntry to the tech's unlocks
    // array here — before BuildRecipeLockIndex reads them.
    void InjectBookRecipeUnlocks() {
        foreach (var node in nodes) {
            if (!Db.bookRecipeIdByTechId.TryGetValue(node.id, out int recipeId)) continue;
            var newEntry = new UnlockEntry { type = "recipe", target = recipeId.ToString() };
            var expanded = new UnlockEntry[node.unlocks.Length + 1];
            node.unlocks.CopyTo(expanded, 0);
            expanded[node.unlocks.Length] = newEntry;
            node.unlocks = expanded;
        }
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

    // Called from Start() — after Db.Awake has populated jobByName / structTypeByName.
    // Central validator for all job-unlock wiring (tech gates AND one-way building gates):
    // flags typos, orphans (locked but nothing unlocks it), and dead gates (unlock wiring on a
    // job that isn't defaultLocked, so the gate never bites).
    void ValidateJobUnlocks() {
        foreach (var kv in jobToTechNode) {
            if (Db.GetJobByName(kv.Key) == null) {
                if (nodeById.TryGetValue(kv.Value, out var node))
                    Debug.LogError($"Tech '{node.name}' job unlock has unknown target '{kv.Key}'");
            }
        }
        foreach (Job j in Db.jobs) {
            if (j == null) continue;
            bool techGated     = jobToTechNode.ContainsKey(j.name);
            bool buildingGated = !string.IsNullOrEmpty(j.unlockedByBuilding);
            if (buildingGated && !Db.structTypeByName.ContainsKey(j.unlockedByBuilding))
                Debug.LogError($"Job '{j.name}' unlockedByBuilding references unknown building '{j.unlockedByBuilding}'.");
            // Orphan: locked but nothing (tech or building) unlocks it — would stay hidden forever.
            if (j.defaultLocked && !techGated && !buildingGated)
                Debug.LogError($"Job '{j.name}' is defaultLocked but nothing unlocks it (no tech, no building gate) — it will never appear in the jobs panel.");
            // Dead gate: unlock wiring exists but the job isn't locked, so it shows from the start.
            if (!j.defaultLocked && (techGated || buildingGated))
                Debug.LogError($"Job '{j.name}' has an unlock gate (tech or building) but isn't defaultLocked — the gate is a no-op.");
        }
    }

    // Called from World.Update every second.
    public void TickUpdate() {
        float decayedTotal = 0f;
        foreach (var node in nodes) {
            if (progress.TryGetValue(node.id, out float p) && p > 0f) {
                float np = Mathf.Max(0f, p - DecayRate);
                decayedTotal += p - np;
                progress[node.id] = np;
            }
        }
        // Feed the research chart's downward (decay) bar with the actual progress lost.
        if (decayedTotal > 0f) StatsTracker.instance?.Record("research_decayed", decayedTotal);
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
                OnTechUnlocked?.Invoke(node);
            } else if (unlocked && p < node.cost * ForgetThreshold) {
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

    // True if the tech with this name is fully researched. Used by onboarding (PlayerTask).
    public bool IsUnlockedByName(string techName) {
        foreach (var node in nodes)
            if (node.name == techName) return unlockedIds.Contains(node.id);
        return false;
    }

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

    // True if any animal is currently adding progress to this tech — i.e. running a
    // ResearchTask whose current objective is the ResearchObjective (actively working at
    // the bench). Excludes the travel leg so the cost text only turns green once points
    // are actually rising.
    public bool IsActivelyResearched(int id) {
        var ac = AnimalController.instance;
        if (ac == null || ac.animals == null) return false;
        foreach (var a in ac.animals) {
            if (a?.task is ResearchTask rt
                && rt.studyTargetId == id
                && rt.currentObjective is ResearchObjective) return true;
        }
        return false;
    }

    // Called when a scientist picks up a ResearchTask.
    // Returns the node ID the scientist should work on, or -1 if nothing to do.
    //
    // Priority: (1) Studied techs below MaintainThreshold × cost (need maintenance/research) —
    //               oldest-unlocked first. Never-unlocked techs tie at int.MaxValue — still picked
    //               if no older candidate exists, so the queue progresses through new techs, but an
    //               already-unlocked tech that's drifted into the maintenance band is topped up first.
    //           (2) Studied techs above MaintainThreshold × cost but below 2×cost — lowest progress %
    //               first (spread reinforcement evenly).
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

            if (p < node.cost * MaintainThreshold) {
                // Below the maintenance threshold — needs work (first unlock, or a slightly-
                // decayed unlock to top back up). Prioritise oldest-unlocked. A never-unlocked
                // tech sits below cost with stamp = MaxValue, so an unlocked tech in the
                // maintenance band (real, lower stamp) is kept up before a new tech is started.
                // bestBelow < 0 seeds the first candidate so MaxValue-tied never-unlocked
                // techs still win over the fallback reinforcement branch.
                int stamp = unlockTimestamps.TryGetValue(id, out int s) ? s : int.MaxValue;
                if (bestBelow < 0 || stamp < bestBelowStamp) {
                    bestBelow = id;
                    bestBelowStamp = stamp;
                }
            } else if (p < cap) {
                // Above the maintenance threshold, below 2×cost — reinforce lowest % first.
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
        float before = GetProgress(targetId);
        progress[targetId] = Mathf.Min(before + workEfficiency * ScientistRate, GetCap(node));
        // Record the actual (clamped) gain for the research chart's scientist series.
        float applied = progress[targetId] - before;
        if (applied > 0f) StatsTracker.instance?.Record("research_gained", applied);
        CheckTransitions();
    }

    // Called from Blueprint.Complete each time a building finishes construction (gameplay path only,
    // not load/worldgen). Grants ConstructionResearchRate × build-labour × scale to the tech that
    // gates the building, so a costlier (longer) build teaches proportionally more.
    // Passive gain is maintain-only: caps at 2×cost and cannot unlock a locked tech — which is fine,
    // because a locked building cannot be constructed in the first place.
    //
    // Build labour = structType.constructionCost (ticks of one builder's labour; mirrors Blueprint's
    // default-2 when unset). `scale` is the fraction of a full build's labour: 1.0 for a fresh build,
    // repairAmount × RepairLaborFraction for a MaintenanceTask. Net effect: research is granted per
    // tick of construction/repair labour at a uniform rate, so it scales with construction time.
    public void AddConstructionProgress(string buildingName, float scale = 1f) {
        if (!buildingToTechNode.TryGetValue(buildingName, out int id)) return;
        if (!nodeById.TryGetValue(id, out var node)) return;
        float labour = Db.structTypeByName.TryGetValue(buildingName, out var st) && st.constructionCost > 0f
                       ? st.constructionCost : 2f;
        float gain   = ConstructionResearchRate * labour * scale;
        float before = GetProgress(id);
        progress[id] = Mathf.Min(before + gain, GetCap(node));
        float applied = progress[id] - before;
        if (applied > 0f) StatsTracker.instance?.Record("research_passive", applied);
        CheckTransitions();
    }

    // Generic raw-amount passive progress primitive. Callers compute the amount: the craft path
    // passes PassiveCraftRate × recipe.workload so research accrues per unit of time worked rather
    // than per cycle (short and long research recipes yield the same rate).
    public void AddPassiveProgress(string researchName, float amount) {
        foreach (var node in nodes) {
            if (node.name == researchName) {
                float before = GetProgress(node.id);
                progress[node.id] = Mathf.Min(before + amount, GetCap(node));
                float applied = progress[node.id] - before;
                if (applied > 0f) StatsTracker.instance?.Record("research_passive", applied);
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

    // Display name of the tech node that gates this recipe, or null if the recipe is
    // ungated. Used by the Recipes panel detail pane to show "needs <research>".
    public string GetUnlockResearchName(int recipeId) {
        if (!recipeToTechNode.TryGetValue(recipeId, out int nodeId)) return null;
        return nodeById.TryGetValue(nodeId, out ResearchNodeData node) ? node.name : null;
    }

    // True if the job is unlocked: either no tech gates it (via an unlocks entry), or
    // its gating tech is currently unlocked. Used by AnimalController to decide whether
    // to show a job row in the jobs panel.
    public bool IsJobUnlocked(string jobName) {
        if (!jobToTechNode.TryGetValue(jobName, out int techNodeId)) return true;
        return unlockedIds.Contains(techNodeId);
    }

    // True if the building is unlocked: either no tech gates it, or its gating tech is
    // currently unlocked. Used by RecipePanel to hide a workstation's recipes until the
    // building is reachable (researched or already placed).
    public bool IsBuildingUnlocked(string buildingName) {
        if (buildingName == null || !buildingToTechNode.TryGetValue(buildingName, out int techNodeId)) return true;
        return unlockedIds.Contains(techNodeId);
    }

    // Applies the gameplay effect of a technology (called on unlock).
    // Animal recipe filters query IsRecipeUnlocked live and RecipePanel rebuilds on open, so
    // recipe gating itself needs no action here. We still discover the recipe's output items
    // so newly-reachable products appear in the inventory tree before any have been crafted.
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
                    DiscoverRecipeOutputs(node, e.target);
                    break;
                default:
                    Debug.LogError($"Tech '{node.name}' has unknown unlock type '{e.type}'");
                    break;
            }
        }
    }

    // Reveals every output item of the recipe in the inventory tree.
    // Runs on both fresh unlock (CheckTransitions → ApplyEffect) and save load (ReapplyAllEffects),
    // so loading a save with unlocked techs also populates discovered items for their recipes.
    void DiscoverRecipeOutputs(ResearchNodeData node, string target) {
        if (!int.TryParse(target, out int rid)) {
            Debug.LogError($"Tech '{node.name}' recipe unlock has non-integer target '{target}'");
            return;
        }
        if (rid < 0 || rid >= Db.recipes.Length) return;
        Recipe recipe = Db.recipes[rid];
        if (recipe == null || recipe.outputs == null) return;
        var ic = InventoryController.instance;
        if (ic == null) return; // startup/test — discovery re-derives from inventory on first TickUpdate
        foreach (var iq in recipe.outputs)
            ic.DiscoverItem(iq.item);
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

    // Debug (/research [id]): fully research one node, recursively maxing its prereqs
    // first so the unlock graph stays consistent (you can't have an unlocked tech whose
    // prereqs aren't). Fires OnTechUnlocked per newly-unlocked node, mirroring the normal
    // CheckTransitions path — so each gets its usual feed message + chime.
    // Returns false if no node has this id.
    public bool MaxTech(int id) {
        if (!nodeById.TryGetValue(id, out var node)) return false;
        MaxNodeRecursive(node);
        return true;
    }

    void MaxNodeRecursive(ResearchNodeData node) {
        if (IsUnlocked(node.id)) return; // already done — and by induction so are its prereqs
        foreach (int prereq in node.prereqs)
            if (nodeById.TryGetValue(prereq, out var pn)) MaxNodeRecursive(pn);
        progress[node.id] = GetCap(node);
        unlockedIds.Add(node.id);
        unlockTimestamps[node.id] = ++unlockCounter;
        ApplyEffect(node);
        OnTechUnlocked?.Invoke(node);
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
