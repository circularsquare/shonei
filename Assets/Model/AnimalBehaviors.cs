using UnityEngine;
using System;
using System.Collections.Generic;

public class AnimalBehaviors {
    private Animal animal;

    public AnimalBehaviors(Animal animal) {
        this.animal = animal;
    }

    public void FindWork() {
        if (animal.job.name == "none") {
            animal.StartDropping();
            animal.Refresh();
            return;
        }

        // 1. Check for blueprints needing construction
        Tile constructionTile = animal.nav.FindConstructingBlueprint(animal.job);
        if (constructionTile != null) {
            Path constructionPath = animal.nav.FindPathConstructingBlueprint(animal.job);
            if (constructionPath != null) {
                animal.SetWorkTile(constructionTile);
                animal.objective = Animal.Objective.Construct;
                animal.GoTo(constructionTile);
                return;
            } else {
                // find adjacent tile to blueprint
                Tile[] adjacents = constructionTile.GetAdjacents();
                Path shortestConstructionPath = null;
                float shortestPathCost = 1000000f;
                foreach (Tile adjacent in adjacents) {
                    if (adjacent != null && adjacent.node.standable) {
                        Path candidatePath = animal.nav.FindPathTo(adjacent);
                        if ((candidatePath != null) && (candidatePath.cost < shortestPathCost)) {
                            shortestConstructionPath = candidatePath;
                            shortestPathCost = candidatePath.cost;
                        }
                    }
                }
                if (shortestConstructionPath != null) {
                    constructionPath = shortestConstructionPath;
                    animal.SetWorkTile(constructionTile);
                    animal.objective = Animal.Objective.Construct;
                    animal.GoTo(constructionPath);
                    Debug.Log("travelling to adjacent tile of blueprint!");
                }
            }
        }

        // 1.5: Check for blueprints that need resources
        Path blueprintPath = animal.nav.FindReceivingBlueprint(animal.job);
        if (blueprintPath != null) {
            animal.deliveryTarget = Animal.DeliveryTarget.Blueprint;
            animal.StartFetching();
            return;
        }

        // 2. Check for harvestable resources matching job
        Path harvestPath = animal.nav.FindHarvestable(animal.job);
        if (harvestPath != null) {
            animal.SetWorkTile(harvestPath.tile);
            animal.GoTo(animal.workTile);
            return;
        }

        // 3. if hauler, haul
        if (animal.job.name == "hauler") {
            animal.StartDropping();
            animal.deliveryTarget = Animal.DeliveryTarget.Storage;
            animal.StartFetching();
            return;
        }

        // 4. start crafting
        TryStartCrafting();
    }

    private bool TryStartCrafting() {
        animal.recipe = PickRecipe();
        if (animal.recipe == null) { return false; }

        Path p = null;
        if (Db.structTypeByName.ContainsKey(animal.recipe.tile)) {
            p = animal.nav.FindBuilding(Db.structTypeByName[animal.recipe.tile]);
        }

        if (p == null) { return false; }

        animal.numRounds = CalculateWorkPossible(animal.recipe);
        if (animal.inv.ContainsItems(animal.recipe.inputs, animal.numRounds)) {
            animal.SetWorkTile(p.tile);
            animal.GoTo(p.tile);
            return true;
        } else {
            foreach (ItemQuantity input in animal.recipe.inputs) {
                if (!animal.inv.ContainsItem(input, animal.numRounds)) {
                    if (input == null) { Debug.LogError("recipe input is null??"); }
                    animal.deliveryTarget = Animal.DeliveryTarget.Self;
                    animal.StartFetching(input.item, input.quantity * animal.numRounds);
                    return false;
                }
            }
            Debug.LogError("can't find crafting input!");
            return false;
        }
    }

    public bool Collect() {
        if (animal.recipe == null) { Debug.LogError("lost recipe!"); return false; }
        foreach (ItemQuantity input in animal.recipe.inputs) {
            if (!animal.inv.ContainsItem(input, animal.numRounds)) {
                animal.deliveryTarget = Animal.DeliveryTarget.Storage;
                return animal.StartFetching(input.item);
            }
        }
        animal.GoTo(animal.workTile);
        return true;
    }

    public Recipe PickRecipe() {
        if (animal.job.recipes.Length == 0) { return null; }
        float maxScore = 0;
        Recipe bestRecipe = null;
        foreach (Recipe recipe in animal.job.recipes) {
            if (recipe != null && animal.ginv.SufficientResources(recipe.inputs)) {
                float score = recipe.Score();
                if (score > maxScore) {
                    maxScore = score;
                    bestRecipe = recipe;
                }
            }
        }
        return bestRecipe;
    }

    public int CalculateWorkPossible(Recipe recipe) {
        if (recipe.inputs.Length == 0) { return -1; }
        int numRounds = 10;
        int n;
        foreach (ItemQuantity input in recipe.inputs) {
            n = animal.ginv.Quantity(input.id) / input.quantity;
            if (n < numRounds) { numRounds = n; }
        }
        foreach (ItemQuantity output in recipe.outputs) {
            Path storePath = animal.nav.FindStorage(output.item);
            if (storePath == null) { n = 0; }
            else {
                n = storePath.tile.GetStorageForItem(output.item) / output.quantity;
            }
            if (n < numRounds) { numRounds = Math.Max(n, 1); }
        }
        return numRounds;
    }
} 