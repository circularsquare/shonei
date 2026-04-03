using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

using AnimalState = Animal.AnimalState;

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
            // Try job swap every 5 ticks when truly idle
            if (animal.tickCounter % 5 == 3) {
                JobSwapper.TrySwap(animal);
            }
            // De-stack: if sharing a tile with another mouse, try to walk to a random reachable
            // neighbour. Only attempt 70% of the time so a large stack doesn't move in lockstep.
            // Skips the random walk below — that's for non-stacked idle behaviour.
            Tile here = animal.TileHere();
            if (here != null && AnimalController.instance.AnyOtherAnimalOnTile(here, animal)) {
                if (UnityEngine.Random.value < 0.70f) {
                    Tile[] neighbours = here.GetAdjacents();
                    // Prefer a neighbour with no other mice; fall back to any reachable neighbour.
                    Tile dest = PickRandomReachableNeighbour(neighbours, preferEmpty: true)
                             ?? PickRandomReachableNeighbour(neighbours, preferEmpty: false);
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

    // Returns a random standable, reachable neighbour from `tiles`.
    // If preferEmpty is true, only considers tiles with no other animals on them.
    private Tile PickRandomReachableNeighbour(Tile[] tiles, bool preferEmpty) {
        var ac = AnimalController.instance;
        var candidates = new System.Collections.Generic.List<Tile>();
        foreach (Tile t in tiles) {
            if (t == null || !t.node.standable) continue;
            if (!animal.nav.CanReach(t)) continue;
            if (preferEmpty && ac.AnyOtherAnimalOnTile(t, animal)) continue;
            candidates.Add(t);
        }
        if (candidates.Count == 0) return null;
        return candidates[UnityEngine.Random.Range(0, candidates.Count)];
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

    // Returns the skill domain for the animal's current task, or null if the task
    // has no associated skill (e.g. hauling, idle wandering).
    private static Skill? GetTaskSkill(Animal animal) {
        Task t = animal.task;
        if (t is HarvestTask)   return Skill.Farming;
        if (t is ConstructTask) return Skill.Construction;
        if (t is ResearchTask)  return Skill.Science;
        if (t is CraftTask craftTask && craftTask.recipe?.skill != null)
            if (System.Enum.TryParse<Skill>(craftTask.recipe.skill, ignoreCase: true, out Skill s)) return s;
        return null;
    }

    private void HandleWorking() {
        Skill? taskSkill      = GetTaskSkill(animal);
        float  baseWorkEff    = ModifierSystem.instance.GetBaseWorkEfficiency(animal);
        float  workEfficiency = ModifierSystem.instance.GetWorkMultiplier(animal, taskSkill);

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
            if (RecipePanel.instance != null && !RecipePanel.instance.IsAllowed(recipe.id)) {
                craftTask.Fail();
                return;
            }
            animal.workProgress += workEfficiency;
            if (taskSkill.HasValue) animal.skills.GainXp(taskSkill.Value, baseWorkEff * 0.1f);
            while (animal.workProgress >= recipe.workload) {
                animal.workProgress -= recipe.workload;
                if (animal.CanProduce(recipe)) {
                    // Consume inputs and produce outputs, rolling chance for each output
                    foreach (ItemQuantity iq in recipe.inputs) animal.Consume(iq.item, iq.quantity);
                    foreach (ItemQuantity output in recipe.outputs) {
                        if (output.chance >= 1f || UnityEngine.Random.value < output.chance)
                            animal.Produce(output.item, output.quantity);
                    }
                    // Pump buildings drain water from their source tile on each completed round
                    if (craftTask.workplace?.building is PumpBuilding pump) pump.DrainForCraft();
                    // Passive research progress from this recipe cycle
                    if (recipe.research != null)
                        ResearchSystem.instance?.AddPassiveProgress(recipe.research, recipe.skillPoints);
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
        } else if (animal.task is ResearchTask rt) {
            animal.workProgress += workEfficiency;
            animal.skills.GainXp(Skill.Science, baseWorkEff * 0.1f);
            ResearchSystem.instance?.AddScientistProgress(workEfficiency, rt.maintenanceTargetId);
            if (animal.workProgress < 10f) return;
            animal.workProgress = 0f;
            animal.task.Complete();
        } else {
            Debug.Log(animal.aName + " in working state but no work to do");
        }
    }

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
                if (totalFood <= ac.na * 400) return; // 4 liang per mouse (400 fen)
                float p = ac.na;
                float pmax = ac.populationCapacity;
                float birthChance = 0.2f * (pmax - p) / pmax;
                if ((float)animal.random.NextDouble() < birthChance) {
                    ac.AddAnimal(animal.x, animal.y);
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
        var obj = animal.task.currentObjective as LeisureObjective;
        if (obj == null) { Debug.LogError($"{animal.aName} in Leisuring state but objective is not LeisureObjective"); animal.task.Fail(); return; }

        var chat = animal.task as ChatTask;

        // Chat-specific: wait for initiator arrival, grant early happiness
        if (chat?.partner != null) {
            if (!chat.chatStarted) return;
            if (!chat.socializedEarly && animal.workProgress >= 7f) {
                chat.socializedEarly = true;
                animal.happiness.NoteSocialized();
            }
        }

        animal.workProgress += 1f;
        if (animal.workProgress >= obj.duration) {
            animal.task.Complete();
            return;
        }

        // Check partner after duration check — if both finish on the same tick,
        // the second to process should complete, not see the first as "gone".
        if (chat?.partner != null) {
            var partnerTask = chat.partner.task as ChatTask;
            if (partnerTask == null || partnerTask.partner != animal) {
                if (chat.socializedEarly) { animal.task.Complete(); } else { animal.task.Fail(); }
            }
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
                    // Arrived at destination
                    animal.x = animal.target.x;
                    animal.y = animal.target.y;
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