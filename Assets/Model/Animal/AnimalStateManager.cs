using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

using AnimalState = Animal.AnimalState;

// Per-tick state machine dispatcher for Animal. Routes UpdateState/UpdateMovement
// to the right handler (Idle/Working/Traveling/Leisuring/Eeping/Falling/...) and
// owns the state-specific side effects (needs decay, task progress, fall physics).
public class AnimalStateManager {
    private Animal animal;

    public AnimalStateManager(Animal animal) {
        this.animal = animal;
    }

    public void UpdateState() { // called every animal.tickupdate!
        switch (animal.state){ // fetching and delivering and such are handled in Update() cuz they require deltatime?
            case AnimalState.Idle:
                HandleIdle();
                break;
            case AnimalState.Working:
                HandleWorking();
                break;
            case AnimalState.Eeping:
                HandleEeping();
                break;
            case AnimalState.Leisuring:
                HandleLeisure();
                break;
            case AnimalState.Traveling:
                HandleTraveling();
                break;
        }
    }

    private void HandleIdle() {
        if (animal.pendingRefresh) {
            animal.pendingRefresh = false;
            animal.Refresh();
            return;
        }
        animal.ChooseTask();
        if (animal.state == AnimalState.Idle) {
            // Try job swap every 2 ticks when truly idle. Cadence is ~2.5× the prior
            // every-5-ticks rate to compensate for the idle-only partner filter in
            // JobSwapper, which makes individual attempts less likely to find a match.
            if (animal.tickCounter % 2 == 1) {
                JobSwapper.TrySwap(animal);
            }
            // De-stack: if sharing a tile with another mouse, try to walk to a direct nav-graph
            // neighbour. Only attempt 70% of the time so a large stack doesn't move in lockstep.
            // Skips the random walk below — that's for non-stacked idle behaviour.
            Tile here = animal.TileHere();
            if (here != null && AnimalController.instance.AnyOtherAnimalOnTile(here, animal)) {
                if (UnityEngine.Random.value < 0.70f) {
                    Tile dest = PickRandomNavNeighbour(here);
                    if (dest != null) {
                        animal.task = new GoTask(animal, dest);
                        if (!animal.task.Start()) animal.task = null;
                    }
                }
                return;
            }
            // Random walking when nothing else to do — prefer tiles without mice,
            // only consider direct nav-graph neighbours (no detours via ladders etc.)
            if (here != null && UnityEngine.Random.Range(0, 5) == 0) {
                Tile dest = PickRandomNavNeighbour(here);
                if (dest != null) {
                    animal.task = new GoTask(animal, dest);
                    if (!animal.task.Start()) animal.task = null;
                }
            }
        }
    }

    // Returns a random direct nav-graph neighbour tile (no waypoints).
    // Prefers tiles with no other animals; returns null if none are available.
    private Tile PickRandomNavNeighbour(Tile here) {
        var ac = AnimalController.instance;
        List<Tile> candidates = null;
        foreach (Node n in here.node.neighbors) {
            if (n.isWaypoint || n.tile == null || !n.standable) continue;
            if (ac.AnyOtherAnimalOnTile(n.tile, animal)) continue;
            if (candidates == null) candidates = new List<Tile>();
            candidates.Add(n.tile);
        }
        if (candidates != null && candidates.Count > 0)
            return candidates[UnityEngine.Random.Range(0, candidates.Count)];
        return null; // don't wander if all neighbours have mice
    }

    // Walks a group item's leaf tree and returns the first leaf with at least `needed` on
    // the animal. Used by the MaintenanceTask completion path when a cost is a group item
    // (e.g. "wood") — Task.PickSupplyLeaf committed to a single leaf at fetch time,
    // so one will be present. Returns null if none found (genuine bug — log at call site).
    private static Item FindLeafInInventory(Animal animal, Item groupItem, int needed) {
        if (groupItem.children == null || groupItem.children.Length == 0)
            return animal.inv.Quantity(groupItem) >= needed ? groupItem : null;
        foreach (Item child in groupItem.children) {
            Item leaf = FindLeafInInventory(animal, child, needed);
            if (leaf != null) return leaf;
        }
        return null;
    }

    // Returns the skill domain for the animal's current task, or null if the task
    // has no associated skill (e.g. hauling, idle wandering).
    private static Skill? GetTaskSkill(Animal animal) {
        Task t = animal.task;
        if (t is HarvestTask)   return Skill.Farming;
        if (t is ConstructTask) return Skill.Construction;
        if (t is MaintenanceTask) return Skill.Construction;
        if (t is ResearchTask)  return Skill.Science;
        if (t is CraftTask craftTask && craftTask.recipe?.skill != null)
            if (System.Enum.TryParse<Skill>(craftTask.recipe.skill, ignoreCase: true, out Skill s)) return s;
        return null;
    }

    private void HandleWorking() {
        Skill? taskSkill      = GetTaskSkill(animal);
        float  baseWorkEff    = ModifierSystem.GetBaseWorkEfficiency(animal);
        float  workEfficiency = ModifierSystem.GetWorkMultiplier(animal, taskSkill);

        if (animal.task is HarvestTask harvestTask) {
            Plant plant = harvestTask.tile.plant;
            if (plant == null || !plant.harvestable) { harvestTask.Fail(); return; }
            animal.workProgress += workEfficiency;
            animal.skills.GainXp(Skill.Farming, baseWorkEff * 0.1f);
            if (animal.workProgress < plant.plantType.harvestTime) { return; }
            animal.workProgress -= plant.plantType.harvestTime;
            animal.Produce(plant.Harvest());
            harvestTask.Complete();
            return;
        } else if (animal.task is ConstructTask constructTask){
            Blueprint blueprint = constructTask.blueprint;
            if (blueprint == null || blueprint.cancelled) {constructTask.Fail(); return;}
            float progressAmount = workEfficiency;
            float progressAfter = blueprint.constructionProgress + progressAmount;
            if (progressAfter >= blueprint.constructionCost) {
                if (blueprint.structType.isTile && AnimalController.instance.IsAnimalOnTile(blueprint.tile)) {
                    constructTask.Fail(); // leave blueprint intact so another mouse can retry later
                    return;
                }
                // Safety net: block if items above would fall (e.g. items appeared after task was created).
                if (blueprint.WouldCauseItemsFall()) {
                    constructTask.Fail(); return;
                }
                if (blueprint.StorageNeedsEmptying()) {
                    constructTask.Fail(); return;
                }
                // Safety net: support beneath the blueprint may have vanished mid-construction
                // (e.g. one of two platforms holding up a 2-wide windmill got deconstructed).
                // The order's isActive=ConditionsMet check filters dispatch but can't stop a task
                // that's already running — block completion so a structure doesn't materialise
                // unsupported. The blueprint sits idle until support returns; a fresh
                // ConstructTask will then re-dispatch and finish the remaining work.
                if (!constructTask.deconstructing && blueprint.IsSuspended()) {
                    constructTask.Fail(); return;
                }
            }
            animal.skills.GainXp(Skill.Construction, baseWorkEff * 0.1f);
            if (blueprint.ReceiveConstruction(progressAmount)){
                var output = blueprint.pendingOutput;
                constructTask.Complete();
                if (output != null)
                    foreach (var iq in output)
                        animal.Produce(iq.item, iq.quantity);
            }
            return;
        } else if (animal.task is CraftTask craftTask) {
            Recipe recipe = craftTask.recipe;
            if (!recipe.IsEligibleForPicking()) {
                craftTask.Fail();
                return;
            }
            // Power boost: workstations whose StructType declares powerBoost > 1 multiply
            // the work-tick rate when their PowerSystem network is currently allocating
            // them full demand. Scoped to CraftTask only — other task types ignore power.
            // See SPEC-power.md.
            Building wsBuilding = craftTask.workplace?.building;
            if (wsBuilding != null && wsBuilding.structType.powerBoost > 1f
                    && PowerSystem.instance != null
                    && PowerSystem.instance.IsBuildingPowered(wsBuilding)) {
                workEfficiency *= wsBuilding.structType.powerBoost;
            }
            animal.workProgress += workEfficiency;
            if (taskSkill.HasValue) animal.skills.GainXp(taskSkill.Value, baseWorkEff * 0.1f);
            while (animal.workProgress >= recipe.workload) {
                animal.workProgress -= recipe.workload;
                if (animal.CanProduce(recipe)) {
                    // Consume inputs and produce outputs, rolling chance for each output.
                    // Quarry routes outputs through its captured-tile's extractionProducts
                    // instead of the recipe's (empty) outputs — see Quarry.cs.
                    foreach (ItemQuantity iq in recipe.inputs) animal.Consume(iq.item, iq.quantity);
                    ItemQuantity[] outputs = recipe.outputs;
                    if (craftTask.workplace?.building is Quarry quarry) {
                        var extra = quarry.GetExtractionOutputs();
                        if (extra != null) outputs = extra;
                    }
                    foreach (ItemQuantity output in outputs) {
                        if (output.chance >= 1f || UnityEngine.Random.value < output.chance)
                            animal.Produce(output.item, output.quantity);
                    }
                    // Pump buildings drain water from their source tile on each completed round
                    if (craftTask.workplace?.building is PumpBuilding pump) pump.DrainForCraft();
                    // Passive research progress from this recipe cycle
                    if (recipe.research != null)
                        ResearchSystem.instance?.AddPassiveProgress(recipe.research, recipe.researchPoints);
                    // Track uses and deplete workstation if applicable
                    Building wb = craftTask.workplace?.building;
                    if (wb?.workstation != null && wb.structType.depleteAt > 0) {
                        wb.workstation.uses++;
                        if (wb.workstation.uses >= wb.structType.depleteAt) {
                            Tile depletedTile = craftTask.workplace;
                            wb.Destroy();
                            StructController.instance.Construct(Db.structTypeByName["platform"], depletedTile);
                            craftTask.Complete();
                            return;
                        }
                    }
                    craftTask.roundsRemaining--;
                    if (craftTask.roundsRemaining <= 0) { craftTask.Complete(); return; }
                } else if (animal.inv.ContainsItems(recipe.inputs)) {
                    craftTask.Fail(); return;
                } else {
                    craftTask.Complete(); return; // out of inputs
                }
            }
        } else if (animal.task is MaintenanceTask maintTask) {
            Structure target = maintTask.target;
            if (target == null || target.go == null) { maintTask.Fail(); return; }
            // Tick condition up. RepairWorkPerTick is the base rate; workEfficiency stretches
            // or compresses it the same way as construction/craft.
            float delta = Structure.RepairWorkPerTick * workEfficiency;
            float before = target.condition;
            float newCondition = Mathf.Min(maintTask.targetCondition, before + delta);
            target.condition = newCondition;
            animal.skills.GainXp(Skill.Construction, baseWorkEff * 0.1f);

            // On completion: consume materials from the mender's inventory, fire repaired callback,
            // and refresh tint if we crossed the break threshold upward.
            if (newCondition >= maintTask.targetCondition || newCondition >= 1f) {
                foreach (ItemQuantity cost in target.structType.costs) {
                    int needed = Mathf.CeilToInt(cost.quantity * Structure.RepairCostFraction * maintTask.repairAmount);
                    if (needed <= 0) continue;
                    // Consume from whichever leaf the mender fetched. For leaf costs, that's cost.item
                    // directly; for group costs, we need to find the leaf in inventory (there will be
                    // exactly one thanks to Task.PickSupplyLeaf committing to a single leaf per task).
                    Item consume = cost.item.IsGroup
                        ? FindLeafInInventory(animal, cost.item, needed)
                        : cost.item;
                    if (consume == null) {
                        Debug.LogError($"MaintenanceTask complete: mender {animal.aName} missing {needed} {cost.item.name} for {target.structType.name}");
                        continue;
                    }
                    animal.Consume(consume, needed);
                }
                MaintenanceSystem.instance?.OnRepaired(target);
                target.RefreshTint();
                // Passive research: a full 0→1 repair matches a fresh build of this structure type.
                // repairAmount is the fraction of condition restored by this task (≤ MaxRepairPerTask).
                ResearchSystem.instance?.AddConstructionProgress(target.structType.name, maintTask.repairAmount);
                maintTask.Complete();
            }
            return;
        } else if (animal.task is ResearchTask rt) {
            animal.workProgress += workEfficiency;
            animal.skills.GainXp(Skill.Science, baseWorkEff * 0.1f);
            // 3× research progress bonus when the matching tech book is equipped.
            // Multiplies the research-progress contribution only — workProgress (the study-cycle
            // counter) is unchanged so cycle length stays consistent regardless of book presence.
            const float BookProgressMultiplier = 3f;
            float researchMult = 1f;
            if (Db.bookItemIdByTechId.TryGetValue(rt.studyTargetId, out int bookItemId)
                && animal.bookSlotInv != null
                && animal.bookSlotInv.Quantity(Db.items[bookItemId]) > 0) {
                researchMult = BookProgressMultiplier;
            }
            ResearchSystem.instance?.AddScientistProgress(workEfficiency * researchMult, rt.studyTargetId);
            if (animal.workProgress < 10f) return;
            animal.workProgress = 0f;
            animal.task.Complete();
        } else {
            Debug.Log(animal.aName + " in working state but no work to do");
        }
    }

    // Per-tick birth probability when (pmax-p)/pmax = 1 (early-game, lots of headroom).
    // Derivation: net eep gain while sleeping ≈ eepRate-tireRate = 1.9/tick; sleep cycle is
    // (maxEep-eepyThreshold)/1.9 ≈ 16 sleep-ticks per ~316-tick wake-sleep cycle, which works
    // out to ~12 sleep-ticks per in-game day (240 ticks). For ~25% chance/day at full breeding
    // factor: 1 - (1-p)^12 = 0.25  →  p ≈ 0.0237.
    private const float MaxBirthChancePerSleepTick = 0.024f;

    private void HandleEeping() {
        animal.eeping.Eep(1f, animal.AtHome());
        // reproduction: logistic growth, gated by population, housing capacity, and food supply
        if (animal.AtHome()) {
            AnimalController ac = AnimalController.instance;
            if (ac.na < ac.populationCapacity && ac.na < ac.totalHousingCapacity) {
                // Require global food > 4 × population before allowing births
                int totalFood = 0;
                GlobalInventory ginv = GlobalInventory.instance;
                foreach (Item food in Db.edibleItems) totalFood += ginv.Quantity(food);
                // Guard the birth roll only — must not early-return, the wake-up check below runs every tick.
                if (totalFood > ac.na * 400) { // 4 liang per mouse (400 fen)
                    float p = ac.na;
                    float pmax = ac.populationCapacity;
                    float birthChance = MaxBirthChancePerSleepTick * (pmax - p) / pmax;
                    if ((float)animal.random.NextDouble() < birthChance) {
                        ac.AddAnimal(animal.x, animal.y);
                    }
                }
            }
        }
        if (animal.eeping.eep >= animal.eeping.maxEep) {
            animal.task.Complete();
        }
    }

    private void HandleTraveling() {
        if (animal.task == null) {
            animal.go.SetActive(true);
            animal.state = AnimalState.Idle;
            return;
        }
        var obj = animal.task.currentObjective as TravelingObjective;
        if (obj == null) {
            Debug.LogError($"{animal.aName} in Traveling state but currentObjective is not TravelingObjective");
            animal.go.SetActive(true);
            animal.state = AnimalState.Idle;
            return;
        }
        animal.workProgress += 1f;
        if (animal.workProgress >= obj.durationTicks) {
            animal.go.SetActive(true); // reappear at the market portal tile
            animal.task.Complete();
        }
    }

    private void HandleLeisure() {
        if (animal.task == null) { animal.state = AnimalState.Idle; return; }

        // Chat has its own handler — dispatch if the current objective is a ChatObjective
        if (animal.task.currentObjective is ChatObjective co) { HandleChatting(co); return; }

        var obj = animal.task.currentObjective as LeisureObjective;
        if (obj == null) { Debug.LogError($"{animal.aName} in Leisuring state but objective is not LeisureObjective"); animal.task.Fail(); return; }

        // Per-tick social grant for socialWhenShared buildings (e.g. fireplace) when another mouse is present.
        // Either mouse having social < 2.0 is enough to start chatting; neither may be > 4.0.
        obj.isSocializing = false;
        float socialSat = animal.happiness.GetSatisfaction("social");
        if (socialSat <= 4.0f && animal.task is LeisureTask lt
            && lt.building != null && lt.building.structType.socialWhenShared) {
            AnimalController ac = World.instance.animalController;
            for (int i = 0; i < ac.na; i++) {
                Animal other = ac.animals[i];
                if (other == animal) continue;
                if (other.state != AnimalState.Leisuring) continue;
                if (other.task is LeisureTask otherLt && otherLt.building == lt.building) {
                    float otherSat = other.happiness.GetSatisfaction("social");
                    if (otherSat > 4.0f) continue;
                    // At least one mouse must have social < 2.0 to spark conversation
                    if (socialSat >= 2.0f && otherSat >= 2.0f) continue;
                    obj.isSocializing = true;
                    animal.happiness.NoteSocialized(Happiness.socialTickGrant);
                    break;
                }
            }
        }

        // Per-tick reading happiness during ReadBookTask's read phase. Mirrors the per-tick
        // social grant for chatting — no lump grant in ReadBookTask.Complete.
        if (animal.task is ReadBookTask) {
            animal.happiness.NoteRead(Happiness.readingTickGrant);
        }

        animal.workProgress += 1f;
        if (animal.workProgress >= obj.duration) {
            animal.task.Complete();
        }
    }

    // Ticks the chat phase of a ChatTask. Both animals run this independently once
    // they enter ChatObjective; the "waiting for partner" state is detected by
    // checking whether the partner is also in a ChatObjective (no sync flag needed).
    private void HandleChatting(ChatObjective co) {
        Animal partner = co.partner;
        bool partnerChatting = partner.state == AnimalState.Leisuring
            && partner.task?.currentObjective is ChatObjective;

        if (!partnerChatting) {
            // Partner not in chat yet — are they still on their way?
            bool enRoute = partner.task is ChatTask ct && ct.partner == animal;
            if (enRoute) return; // wait for them to arrive
            // Partner abandoned or finished — keep whatever partial satisfaction was earned
            animal.task.Complete();
            return;
        }

        // Both chatting — grant social satisfaction gradually and tick timer
        animal.happiness.NoteSocialized(Happiness.socialTickGrant);
        animal.workProgress += 1f;
        if (animal.workProgress >= co.duration
            || animal.happiness.GetSatisfaction("social") >= Happiness.satisfactionCap) {
            animal.task.Complete();
        }
    }

    public void UpdateMovement(float deltaTime) {
        // Fall unless the current nav edge is deliberately vertical (ladder/cliff/stair)
        Tile tileHere = animal.TileHere();
        if (tileHere == null) {
            Debug.LogError($"{animal.name} is out of bounds at ({animal.x}, {animal.y})!");
            if (animal.state != AnimalState.Falling) animal.nav.Fall();
        } else if (!animal.nav.preventFall && !tileHere.node.standable
            && animal.state != AnimalState.Falling) {
            animal.nav.Fall();
        }
        if (animal.state == AnimalState.Falling) {
            animal.nav.fallVelocity += World.fallGravity * deltaTime;
            float nextY = animal.y - animal.nav.fallVelocity * deltaTime;

            Tile nextTile = animal.world.GetTileAt(animal.x, nextY);

            if (nextTile == null) {
                // Would fall out of world bounds — halt in place
                Debug.LogError($"{animal.aName} fall would exit world bounds at ({animal.x}, {nextY}), stopping.");
                animal.nav.fallVelocity = 0f;
                animal.state = AnimalState.Idle;
            } else if (nextTile.type.solid) {
                // Would enter a solid tile — snap to its top surface
                animal.y = nextTile.y + 1;
                animal.go.transform.position = new Vector3(animal.x, animal.y, animal.z);
                animal.nav.fallVelocity = 0f;
                animal.state = AnimalState.Idle;
            } else {
                // Safe to move — apply and check for standable landing
                animal.y = nextY;
                animal.go.transform.position = new Vector3(animal.x, animal.y, animal.z);
                if (nextTile.node.standable && animal.y <= nextTile.y) {
                    animal.y = nextTile.y;
                    animal.go.transform.position = new Vector3(animal.x, animal.y, animal.z);
                    animal.nav.fallVelocity = 0f;
                    animal.state = AnimalState.Idle;
                }
            }
        } else if (IsMovingState(animal.state)) {
            if (animal.target == null) {
                // Error handling for missing target
                Debug.LogError(animal.aName + " movement target null! failing task " + animal.state.ToString());
                animal.task?.Fail();
                animal.state = Animal.AnimalState.Idle;
            }
            else {
                bool done = animal.nav.Move(deltaTime);
                if (done) {
                    // Arrived at destination. target is a Node — read float wx/wy so off-grid
                    // workspot waypoints (e.g. wheel runner) snap to the waypoint's actual
                    // position, not a rounded integer tile.
                    animal.x = animal.target.wx;
                    animal.y = animal.target.wy;
                    animal.go.transform.position = new Vector3(animal.x, animal.y, animal.z);

                    HandleArrival();
                }
                animal.go.transform.localScale = new Vector3(animal.facingRight ? 1 : -1, 1, 1);
            }
        }
        animal.UpdateCurrentTile();
    }
    private bool IsMovingState(AnimalState state) {
        return state == AnimalState.Moving;
    }
    private void HandleArrival() {
        // Tile here = animal.TileHere();
        // Debug.Log($"Arrived: state={animal.state}, " + 
        //       $"here=({here.x},{here.y}), " +
        //       $"workTile=({animal.workTile?.x},{animal.workTile?.y}), " +
        //       $"equals={here == animal.workTile}");
        switch (animal.state) {
            case AnimalState.Moving: 
                animal.OnArrival();
                break;
        }
    }
}