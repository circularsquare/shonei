using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class CraftTask : Task {
    public Recipe recipe;
    public Tile workplace;
    public int roundsRemaining;
    private List<(Item item, int perRound)> _inputsToFetch;
    private int _fetchInputIndex;
    private readonly Building _building; // always set — assigned by WOM via RegisterWorkstation
    private readonly Recipe _preChosenRecipe; // set by ChooseCraftTask; null → PickRecipeForBuilding fallback

    public CraftTask(Animal animal, Building building, Recipe preChosenRecipe = null) : base(animal){
        _building = building;
        _preChosenRecipe = preChosenRecipe;
    }

    public override bool Initialize(){
        recipe = _preChosenRecipe ?? animal.PickRecipeForBuilding(_building);
        if (recipe == null) { return false; }
        Path p = animal.nav.PathTo(_building.workTile);
        if (!animal.nav.WithinRadius(p, MediumFindRadius)) { return false; }
        workplace = _building.workTile;

        roundsRemaining = animal.CalculateWorkPossible(recipe);
        if (recipe.maxRoundsPerTask > 0 && roundsRemaining > recipe.maxRoundsPerTask)
            roundsRemaining = recipe.maxRoundsPerTask;
        if (roundsRemaining == 0) { return false; }

        // Build the fetch list in forward order (skipping what's already in animal inv)
        _inputsToFetch = new List<(Item, int)>();
        foreach (ItemQuantity iq in recipe.inputs) {
            if (!animal.inv.ContainsItem(iq, roundsRemaining))
                _inputsToFetch.Add((iq.item, iq.quantity));
        }
        _fetchInputIndex = 0;

        // Queue tail: Go → Work → Drops
        objectives.AddLast(new GoObjective(this, workplace));
        objectives.AddLast(new WorkObjective(this, recipe));
        foreach (ItemQuantity output in recipe.outputs)
            objectives.AddLast(new DropObjective(this, output.item));

        // Prepend fetch objectives in reverse so index 0 ends at front
        for (int i = _inputsToFetch.Count - 1; i >= 0; i--) {
            var (item, perRound) = _inputsToFetch[i];
            int toFetch = perRound * roundsRemaining - animal.inv.Quantity(item);
            (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(item);
            if (itemPath == null) { return false; }
            ReserveStack(stack, toFetch);
            objectives.AddFirst(new FetchObjective(this, new ItemQuantity(item, toFetch), itemPath.tile, softFetch: true, sourceInv: stack.inv));
        }

        return true;
    }
    public override void Complete(){
        // When a FetchObjective finishes and we're still in the fetch phase, run continuation logic
        if (currentObjective is FetchObjective && _fetchInputIndex < _inputsToFetch.Count) {
            var (item, perRound) = _inputsToFetch[_fetchInputIndex];
            int have = animal.inv.Quantity(item);
            int needed = perRound * roundsRemaining;

            if (have < needed) {
                int stillNeed = needed - have;
                (Path p, ItemStack stack) = animal.nav.FindPathItemStack(item);
                if (p != null) {
                    ReserveStack(stack, stillNeed);
                    // Pass `needed` (not stillNeed) as iq.quantity so FetchObjective.Start's early-exit
                    // (have >= iq.quantity) only fires when the animal truly has everything it needs.
                    // OnArrival calculates how much to take as (iq.quantity - have), so it still fetches
                    // only the remaining gap.
                    EnqueueFront(new FetchObjective(this, new ItemQuantity(item, needed), p.tile, softFetch: true, sourceInv: stack.inv));
                    // Don't increment — check this ingredient again after the retry
                    base.Complete();
                    return;
                }
                // No sources left — trim rounds to what we can actually do
                int achievable = have / perRound;
                if (achievable == 0) { Fail(); return; }
                Debug.Log($"{animal.aName}: trimming rounds {roundsRemaining}→{achievable} (short on {item.name})");
                roundsRemaining = achievable;
            }
            _fetchInputIndex++;
        }
        base.Complete();
    }
}
