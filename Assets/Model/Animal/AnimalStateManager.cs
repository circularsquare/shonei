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
            // Random walking when nothing else to do
            if (UnityEngine.Random.Range(0, 5) == 0) {
                animal.task = new GoTask(animal,
                    animal.world.GetTileAt(animal.x + UnityEngine.Random.Range(-1, 2), animal.y));
                if (!animal.task.Start()) animal.task = null;
            }
        }
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
        } else if (animal.task is ResearchTask) {
            animal.workProgress += workEfficiency;
            animal.skills.GainXp(Skill.Science, baseWorkEff * 0.1f);
            ResearchSystem.instance?.AddScientistProgress(workEfficiency);
            if (animal.workProgress < 10f) return;
            animal.workProgress = 0f;
            animal.task.Complete();
        } else {
            Debug.Log(animal.aName + " in working state but no work to do");
        }
    }

    private void HandleEeping() {
        animal.eeping.Eep(1f, animal.AtHome());
        // reproduction: logistic growth, gated by population and housing capacity
        if (animal.AtHome()) {
            AnimalController ac = AnimalController.instance;
            if (ac.na < ac.populationCapacity && ac.na < ac.totalHousingCapacity) {
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
            animal.y -= animal.nav.fallVelocity * deltaTime;
            animal.go.transform.position = new Vector3(animal.x, animal.y, 0);
            Tile here = animal.TileHere();
            if (here != null && here.node.standable && animal.y <= here.y) {
                animal.y = here.y;
                animal.go.transform.position = new Vector3(animal.x, animal.y, 0);
                animal.state = AnimalState.Idle;
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
                    animal.go.transform.position = new Vector3(animal.x, animal.y, 0);

                    HandleArrival();
                }
                animal.sr.flipX = !animal.isMovingRight;
            }
        }
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