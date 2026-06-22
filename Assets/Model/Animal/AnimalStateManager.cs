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

    // Per-idle-tick chance for a mouse idling inside its home to head back outside. Keeps the
    // unemployed (who have no work pulling them out) from clustering in their burrow all day.
    private const double WanderOutOfHomeChance = 0.30;

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
        // ChooseTask can park the animal straight into a stationary working pose — e.g. a
        // construct order whose builder is already standing on the footprint tile. On that
        // path the WOM dispatch (ChooseOrder) runs the objective's Start() BEFORE animal.task
        // is assigned, so the Start-time UpdateState() read a null task and missed the
        // objective's ViewOverride/PoseOverride (mouse stays side-facing). Refresh now that
        // animal.task is set so the paper-doll reflects the chosen objective.
        if (animal.task != null) animal.animationController?.UpdateState();
        if (animal.state == AnimalState.Idle) {
            // Try job swap every 2 ticks when truly idle. Cadence is ~2.5× the prior
            // every-5-ticks rate to compensate for the idle-only partner filter in
            // JobSwapper, which makes individual attempts less likely to find a match.
            if (animal.tickCounter % 2 == 1) {
                JobSwapper.TrySwap(animal);
            }
            // A mouse that woke inside its burrow would otherwise sit there: the neighbour-only
            // random walk below can't traverse the door waypoint to leave, so it's effectively
            // trapped (this strands the unemployed in particular — no job pulls them out). Give
            // it a per-tick chance to path to a tile outside the house; once out, the normal idle
            // wander takes over. Pathing (not the neighbour walk) is what lets it cross the door.
            Building home = animal.homeBuilding;
            if (home != null && animal.insideBuilding == home
                    && animal.random.NextDouble() < WanderOutOfHomeChance) {
                Path outPath = animal.nav.FindPathTo(
                    t => t.node.standable && t.interiorBuilding != home,
                    r: Task.MediumFindRadius);
                if (outPath != null) {
                    animal.task = new GoTask(animal, outPath.tile);
                    if (!animal.task.Start()) animal.task = null;
                }
                return;
            }
            // Don't loiter on ladder-only footing (a rung in mid-air) — looks wrong and
            // strands mice after a mid-climb job switch or load. Head for the nearest stable
            // tile (usually the ladder's top/bottom landing). Real work, if any, was already
            // chosen by ChooseTask above and walks the mouse off naturally; this only fires
            // when it would otherwise just sit on the rung.
            Tile foot = animal.TileHere();
            if (foot != null && animal.world.graph.IsLadderOnlyFooting(foot.x, foot.y)) {
                Path escape = animal.nav.FindPathTo(
                    t => t != foot && t.node.standable
                         && !animal.world.graph.IsLadderOnlyFooting(t.x, t.y),
                    r: Task.MediumFindRadius);
                if (escape != null) {
                    animal.task = new GoTask(animal, escape.tile);
                    if (!animal.task.Start()) animal.task = null;
                }
                return;
            }
            // De-stack: if sharing a tile with another mouse, try to walk to a direct nav-graph
            // neighbour. Only attempt 70% of the time so a large stack doesn't move in lockstep.
            // Skips the random walk below — that's for non-stacked idle behaviour.
            Tile here = animal.TileHere();
            if (here != null && AnimalController.instance.AnyOtherAnimalOnTile(here, animal)) {
                if (animal.random.NextDouble() < 0.70) {
                    Tile dest = PickRandomNavNeighbour(animal.PathStartNode());
                    if (dest != null) {
                        animal.task = new GoTask(animal, dest);
                        if (!animal.task.Start()) animal.task = null;
                    }
                }
                return;
            }
            // Random walking when nothing else to do — prefer tiles without mice,
            // only consider direct nav-graph neighbours (no detours via ladders etc.)
            if (here != null && animal.random.Next(0, 5) == 0) {
                Tile dest = PickRandomNavNeighbour(animal.PathStartNode());
                if (dest != null) {
                    animal.task = new GoTask(animal, dest);
                    if (!animal.task.Start()) animal.task = null;
                }
            }
        }
    }

    // Returns a random tile-backed standable neighbour reachable from `startNode`,
    // skipping waypoints and tiles occupied by other animals. Caller passes the
    // mouse's logical position node (Animal.PathStartNode) so a mouse standing on an
    // interior waypoint inside a building can pick the door approach as its step out —
    // the interior tile's grid node has no graph edges and would always return null.
    private Tile PickRandomNavNeighbour(Node startNode) {
        if (startNode == null) return null;
        var ac = AnimalController.instance;
        List<Tile> candidates = null;
        foreach (Node n in startNode.neighbors) {
            if (n.isWaypoint || n.tile == null || !n.standable) continue;
            if (ac.AnyOtherAnimalOnTile(n.tile, animal)) continue;
            if (candidates == null) candidates = new List<Tile>();
            candidates.Add(n.tile);
        }
        if (candidates != null && candidates.Count > 0)
            return candidates[animal.random.Next(0, candidates.Count)];
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

    // Per-tick wear on every equip slot while the animal is working. Each stack's
    // EquipDecay reads its item's equipDecayRate (per-year units, like decayRate) and
    // contributes to the same decayCounter that passive decay uses. Items with
    // equipDecayRate == 0 are no-ops, so this is safe to call on slots whose contents
    // aren't meant to wear from use (foodSlotInv, bookSlotInv). Wear is deterministic.
    private static void ApplyEquipDecay(Animal animal) {
        animal.toolSlotInv?.itemStacks[0]?.EquipDecay(1f);
        animal.clothingSlotInv?.itemStacks[0]?.EquipDecay(1f);
        animal.foodSlotInv?.itemStacks[0]?.EquipDecay(1f);
        animal.bookSlotInv?.itemStacks[0]?.EquipDecay(1f);
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
        if (t is WorkProcessorTask wpt && wpt.building?.processor?.recipe?.skill != null)
            if (System.Enum.TryParse<Skill>(wpt.building.processor.recipe.skill, ignoreCase: true, out Skill ws)) return ws;
        return null;
    }

    private void HandleWorking() {
        Skill? taskSkill      = GetTaskSkill(animal);
        float  baseWorkEff    = ModifierSystem.GetBaseWorkEfficiency(animal);
        float  workEfficiency = ModifierSystem.GetWorkMultiplier(animal, taskSkill);
        ApplyEquipDecay(animal);

        if (animal.task is HarvestTask harvestTask) {
            Plant plant = harvestTask.tile.plant;
            if (plant == null || !plant.harvestable) { harvestTask.Fail(); return; }
            animal.workProgress += workEfficiency;
            animal.skills.GainXp(Skill.Farming, baseWorkEff * SkillSet.XpPerWorkTick);
            if (animal.workProgress < plant.plantType.harvestTime) { return; }
            animal.workProgress -= plant.plantType.harvestTime;
            animal.Produce(plant.Harvest());
            harvestTask.Complete();
            return;
        } else if (animal.task is WaterPlantTask waterTask) {
            Plant plant = waterTask.tile.plant;
            if (plant == null) { waterTask.Fail(); return; }
            Tile soil = World.instance.GetTileAt(waterTask.tile.x, waterTask.tile.y - 1);
            if (soil == null || !soil.type.solid) { waterTask.Fail(); return; }
            animal.workProgress += workEfficiency;
            animal.skills.GainXp(Skill.Farming, baseWorkEff * SkillSet.XpPerWorkTick);
            if (animal.workProgress < WaterPlantTask.WaterTime) return;
            animal.workProgress -= WaterPlantTask.WaterTime;
            waterTask.PourWater(soil);
            waterTask.Complete();
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
            animal.skills.GainXp(Skill.Construction, baseWorkEff * SkillSet.XpPerWorkTick);
            if (blueprint.ReceiveConstruction(progressAmount)){
                var output = blueprint.pendingOutput;
                constructTask.Complete();
                if (output != null)
                    foreach (var iq in output)
                        animal.Produce(iq.item, iq.quantity);
                return;
            }
            // Yield after a capped work stint so the builder re-evaluates needs and priorities
            // (eat, sleep, a closer/better task) instead of grinding a long build in one sitting.
            // Partial progress persists on the blueprint and the construct order stays registered,
            // so a fresh ConstructTask resumes where this one left off. Mirrors the craft time cap.
            if (++constructTask.ticksWorked >= Animal.MaxWorkStintTicks)
                constructTask.Complete();
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
            if (taskSkill.HasValue) animal.skills.GainXp(taskSkill.Value, baseWorkEff * SkillSet.XpPerWorkTick);
            while (animal.workProgress >= recipe.workload) {
                animal.workProgress -= recipe.workload;
                if (animal.CanProduce(recipe, wsBuilding)) {
                    // Out of fuel mid-batch (rare — rounds were sized to fetched fuel): stop
                    // cleanly rather than producing a free unfuelled round. Complete, not Fail,
                    // so it's treated like running out of inputs, not an error.
                    if (!craftTask.HasFuelForRound()) { craftTask.Complete(); return; }
                    // Consume inputs and produce outputs, rolling chance for each output.
                    // Extraction buildings (quarry / digging pit) route outputs through
                    // their captured tile's distribution instead of the recipe's (empty)
                    // outputs — see ExtractionBuilding.cs.
                    foreach (ItemQuantity iq in recipe.inputs) animal.Consume(iq.item, iq.quantity);
                    if (craftTask.FuelItem != null) animal.Consume(craftTask.FuelItem, craftTask.FuelPerRoundFen);
                    ItemQuantity[] outputs = recipe.outputs;
                    if (craftTask.workplace?.building is ExtractionBuilding extractor) {
                        var extra = extractor.GetExtractionOutputs();
                        if (extra != null) outputs = extra;
                    }
                    foreach (ItemQuantity output in outputs) {
                        if (output.chance >= 1f || animal.random.NextDouble() < output.chance) {
                            // The workstation may keep the output itself (e.g. a cauldron holds its
                            // brewed tonic) — only what it can't absorb is carried off by the worker.
                            int q = wsBuilding != null ? wsBuilding.TryAbsorbOutput(output.item, output.quantity)
                                                       : output.quantity;
                            if (q > 0) animal.Produce(output.item, q);
                        }
                    }

                    // Pump buildings drain water from their source tile on each completed round
                    if (craftTask.workplace?.building is PumpBuilding pump) pump.DrainForCraft();
                    // Passive research progress from this recipe cycle
                    if (recipe.research != null)
                        ResearchSystem.instance?.AddPassiveProgress(recipe.research, ResearchSystem.PassiveCraftRate * recipe.workload);
                    // Track uses and deplete workstation if applicable
                    Building wb = craftTask.workplace?.building;
                    if (wb?.workstation != null && wb.structType.depleteAt > 0) {
                        wb.workstation.uses++;
                        if (wb.workstation.uses >= wb.structType.depleteAt) {
                            Tile depletedTile = craftTask.workplace;
                            bool wasPit = wb is DiggingPit;
                            wb.Destroy();
                            // Digging pit kept the tile intact during operation (preservesTile)
                            // — on full depletion the dirt is finally gone, so empty the tile
                            // before the platform takes its place. Without this, the follow-up
                            // platform would sit on top of the original solid substrate.
                            if (wasPit) depletedTile.type = Db.tileTypeByName["empty"];
                            StructController.instance.Construct(Db.structTypeByName["platform"], depletedTile);
                            craftTask.Complete();
                            return;
                        }
                        // Digging pit: refresh the dish visual and drop the workspot
                        // so the next craft round shows the new excavation depth and
                        // the digger keeps standing on the receding floor. The animal
                        // is already standing AT workNode (CraftTask arrived) so it
                        // needs an explicit SnapTo — otherwise its transform stays at
                        // the old wy until it walks somewhere else.
                        if (wb is DiggingPit pit) {
                            pit.RebuildDishVisual();
                            if (pit.workNode != null) animal.SnapTo(animal.x, pit.workNode.wy);
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
        } else if (animal.task is WorkProcessorTask wpt) {
            // Tended processor (cauldron): the worker labours on the locked-in batch. Unlike a
            // CraftTask there's no per-round consume/produce here — the inputs already sit in the
            // processor's buffer and the conversion is the single Tap() at the end. Progress lives
            // on the processor (persists across stints + saves), advancing in ~labour-seconds.
            Processor proc = wpt.building?.processor;
            if (wpt.building == null || wpt.building.go == null || proc == null
                    || proc.state != Processor.State.Working) { wpt.Fail(); return; }
            wpt.building.workingAnimal = animal; // keep the craft-gated fire lit while labouring
            proc.progress += workEfficiency;
            if (taskSkill.HasValue) animal.skills.GainXp(taskSkill.Value, baseWorkEff * SkillSet.XpPerWorkTick);
            if (proc.progress >= proc.duration) {
                proc.Tap();                                 // drain buffer (+ fuel) → output, → Tapped
                WorkProcessorTask.RegisterHaulOut(proc);    // haul the finished batch to a tank
                wpt.Complete();
                return;
            }
            // Yield after a capped stint so the worker re-evaluates needs; proc.progress persists
            // and the WorkProcessor order re-fires (state still Working) for the next stint.
            if (++wpt.ticksWorked >= Animal.MaxWorkStintTicks) wpt.Complete();
            return;
        } else if (animal.task is MaintenanceTask maintTask) {
            Structure target = maintTask.target;
            if (target == null || target.go == null) { maintTask.Fail(); return; }
            // Tick condition up. Repair labour scales with the structure's build cost: a full 0→1
            // repair takes RepairLaborFraction × constructionCost ticks at baseline efficiency, so
            // condition rises slower for costlier-to-build structures. workEfficiency stretches or
            // compresses it the same way as construction/craft.
            float buildLabour = target.structType.constructionCost > 0f ? target.structType.constructionCost : 2f;
            float delta = workEfficiency / (Structure.RepairLaborFraction * buildLabour);
            float before = target.condition;
            float newCondition = Mathf.Min(maintTask.targetCondition, before + delta);
            target.condition = newCondition;
            animal.skills.GainXp(Skill.Construction, baseWorkEff * SkillSet.XpPerWorkTick);

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
                // Passive research, granted per tick of repair labour at the same rate as a fresh build.
                // scale = fraction of the full build's labour this repair represents = repairAmount ×
                // RepairLaborFraction. So a full 0→1 repair grants ¼ of a fresh build's research (matching
                // the ¼ labour/materials a full repair costs), and it scales with construction time.
                ResearchSystem.instance?.AddConstructionProgress(target.structType.name,
                    maintTask.repairAmount * Structure.RepairLaborFraction);
                maintTask.Complete();
            }
            return;
        } else if (animal.task is ResearchTask rt) {
            animal.workProgress += workEfficiency;
            animal.skills.GainXp(Skill.Science, baseWorkEff * SkillSet.XpPerWorkTick);
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
            Debug.LogError(animal.aName + " in working state but no work to do");
        }
    }

    // Birth rate is specified as the chance per in-game day, per sleeping mouse, at full breeding
    // factor (the population/food throttles below scale it down). The per-sleep-tick probability is
    // derived from it, so the day-rate stays fixed even if the sleep rates are retuned: a mouse
    // accrues ticksInDay * tireRate/eepRate sleep-ticks per day (the sleepFraction identity in
    // Eeping), and we invert 1 - (1-p)^sleepTicksPerDay = perDay for p.
    private const float BirthChancePerDayAtFullFactor = 0.36f;
    private static readonly float MaxBirthChancePerSleepTick =
        1f - Mathf.Pow(1f - BirthChancePerDayAtFullFactor,
                       Eeping.eepRate / (World.ticksInDay * Eeping.tireRate));

    // Reproduction food gate. Below the hard floor: no births at all.
    // Between floor and full days: chance scales linearly from 0 → MaxBirthChancePerSleepTick.
    private const float BirthFoodHardFloorDays = 2f;
    private const float BirthFoodFullDays      = 10f;

    private void HandleEeping() {
        // Sleep recovery (eeping.Eep) is ticked wall-clock in Animal.HandleNeeds, not here —
        // see the comment there. This handler runs only on the energy-gated UpdateState path,
        // so it keeps just the per-sleep-tick *events*: the reproduction roll and the wake check.
        // reproduction: logistic growth, gated by population, housing capacity, and food supply.
        // Any sleeping mouse can trigger a birth as long as some house anywhere has a free slot —
        // the sleeper doesn't need to be in their own home.
        AnimalController ac = AnimalController.instance;
        if (ac.na < ac.populationCapacity && ac.na < ac.totalHousingCapacity) {
            // Birth gate uses the colony's days-of-food-in-storage stat (cached in
            // UpdateColonyStats). Two-stage: hard floor at 2 days (crisis — no births),
            // then linear taper × clamp(days/10) up to full birth rate at 10 days.
            // Guard the birth roll only — must not early-return, the wake-up check below runs every tick.
            float days = ac.daysOfFoodInStorage;
            if (days >= BirthFoodHardFloorDays) {
                float foodFactor = Mathf.Clamp01(days / BirthFoodFullDays);
                float p = ac.na;
                float pmax = ac.populationCapacity;
                float birthChance = MaxBirthChancePerSleepTick * (pmax - p) / pmax * foodFactor;
                // Double the rate for the first couple of births so a new colony grows past its
                // starting size quickly (see AnimalController.EarlyBirthBoost* for the rationale).
                if (ac.births < AnimalController.EarlyBirthBoostBirths)
                    birthChance *= AnimalController.EarlyBirthBoostMultiplier;
                if ((float)animal.random.NextDouble() < birthChance) {
                    ac.AddAnimal(animal.x, animal.y);
                    ac.births++;
                }
            }
        }
        // Wake-up check. Always wake when fully rested; otherwise, once rested past the wake floor,
        // roll to wake (biased toward staying asleep, and toward deeper sleep at night) — so a mouse
        // wakes in the morning rather than sleeping in to 100%. See Eeping.WakeChance.
        if (animal.eeping.eep >= animal.eeping.maxEep
            || (float)animal.random.NextDouble() < animal.eeping.WakeChance(animal.BedtimeUrgency())) {
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
            && animal.state != AnimalState.Falling
            && tileHere.interiorBuilding == null) {
            // Interior gate: a mouse on a building's hollow interior tile is logically
            // supported by the interior waypoint — the tile underneath is solid (burrow's
            // preserved dirt) or otherwise non-standable, but that's expected. Without this
            // gate, eeping mice inside a burrow get instantly Fall()-ed and snapped up to
            // the surface above the bank.
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
                animal.SnapTo(animal.x, nextTile.y + 1);
                animal.nav.fallVelocity = 0f;
                animal.state = AnimalState.Idle;
            } else {
                // Safe to move — apply and check for standable landing
                animal.SnapTo(animal.x, nextY);
                if (nextTile.node.standable && animal.y <= nextTile.y) {
                    animal.SnapTo(animal.x, nextTile.y);
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
                    animal.SnapTo(animal.target.wx, animal.target.wy);
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