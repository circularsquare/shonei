using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Runs one or more rounds of a recipe at a workstation building. Selected via
// Animal.ChooseCraftTask (recipe-score sorted), not WOM — craft is its own dispatch
// layer between tiers 3 and 4 in ChooseTask.
//
// Queue: Fetch(input₁) → Fetch(input₂) → … → Go(workNode) → Work → Drop(output₁) → Drop(output₂) → …
// Reserves: source ItemStacks for each input (via FetchAndReserve).
// roundsRemaining is capped by recipe.maxRoundsPerTask; mid-task fetch shortfalls
// either retry against a new stack or trim rounds down to what the animal already carries.
public class CraftTask : Task {
    public Recipe recipe;
    public Tile workplace;
    public int roundsRemaining;
    private List<(Item item, int perRound)> _inputsToFetch;
    private int _fetchInputIndex;
    // Fuel committed for this task: one concrete leaf chosen at Initialize (recipe.fuelCost>0).
    // perRound is in FEN — round(fuelCost×100/fuelValue) — to avoid rounding-waste. It rides
    // _inputsToFetch like a normal input, so reserve/fetch/trim/Complete handle it for free;
    // these accessors let the work tick consume it (AnimalStateManager). null = recipe has no fuel.
    public Item FuelItem { get; private set; }
    public int  FuelPerRoundFen { get; private set; }
    public bool HasFuelForRound() => FuelItem == null || animal.inv.Quantity(FuelItem) >= FuelPerRoundFen;
    private readonly Building _building; // always set — assigned by WOM via RegisterWorkstation
    private readonly Recipe _preChosenRecipe; // set by ChooseCraftTask; null → PickRecipeForBuilding fallback

    public CraftTask(Animal animal, Building building, Recipe preChosenRecipe = null) : base(animal){
        _building = building;
        _preChosenRecipe = preChosenRecipe;
    }

    public override bool Initialize(){
        recipe = _preChosenRecipe ?? animal.PickRecipeForBuilding(_building);
        if (recipe == null) { return false; }
        // Path to workNode — equals workTile.node for normal buildings, but for buildings
        // with a workSpot (e.g. wheel) it's an off-grid waypoint at the worker's pose
        // position. workplace stays as the integer workTile for power/occupancy semantics.
        Path p = animal.nav.PathTo(_building.workNode);
        if (!animal.nav.WithinWorkRange(p)) { return false; }
        workplace = _building.workTile;

        roundsRemaining = animal.CalculateWorkPossible(recipe);
        if (recipe.maxRoundsPerTask > 0 && roundsRemaining > recipe.maxRoundsPerTask)
            roundsRemaining = recipe.maxRoundsPerTask;
        if (roundsRemaining == 0) { return false; }

        // Build the fetch list in forward order (skipping what's already in animal inv).
        // A group input (e.g. "wood") commits to a concrete leaf now (surplus × nearness), so the
        // fetch, reservation, and shortfall-retry in Complete all target the same type; if that leaf
        // can't cover every round we trim rounds rather than mixing types mid-session. Consumption
        // at work time resolves the recipe's group input against whatever leaf we carried.
        _inputsToFetch = new List<(Item, int)>();
        foreach (ItemQuantity iq in recipe.inputs) {
            Item fetchItem = ResolveConsumeLeaf(iq.item);
            if (!animal.inv.ContainsItem(new ItemQuantity(fetchItem, iq.quantity), roundsRemaining))
                _inputsToFetch.Add((fetchItem, iq.quantity));
        }
        // Fuel: commit to one concrete fuel leaf (target-aware surplus pick) and append it as a
        // synthetic fetch entry so it reserves/fetches/trims exactly like a real input. CanCraft
        // already confirmed enough fuel energy exists globally; PickFuel can still come back null
        // if it was consumed between gate and here — bail like any unmakeable recipe.
        if (recipe.fuelCost > 0f) {
            Item fuel = GlobalInventory.instance.PickFuel();
            if (fuel == null) { return false; }
            FuelItem = fuel;
            FuelPerRoundFen = Mathf.RoundToInt(recipe.fuelCost * 100f / fuel.fuelValue);
            if (!animal.inv.ContainsItem(new ItemQuantity(fuel, FuelPerRoundFen), roundsRemaining))
                _inputsToFetch.Add((fuel, FuelPerRoundFen));
        }
        _fetchInputIndex = 0;

        // Carry cap: never commit to more rounds than the animal can physically hold. Packs the
        // resolved input + fuel leaves just assembled the way AddItem will. If even one round won't
        // fit the current (possibly cluttered) pack, bail — ChooseTask's clutter-drop trigger frees
        // the slots first. Runs before any ReserveStack below, so an early return leaks nothing.
        roundsRemaining = MaxCarryableRounds(roundsRemaining);
        if (roundsRemaining == 0) { return false; }

        // Queue tail: Go → Work → Drops. GoObjective targets workNode (waypoint or tile-node)
        // so the runner ends up at the workSpot position, not just on the workTile center.
        objectives.AddLast(new GoObjective(this, _building.workNode));
        objectives.AddLast(new WorkObjective(this, recipe));
        foreach (ItemQuantity output in recipe.outputs)
            objectives.AddLast(new DropObjective(this, output.item));

        // Find a source for each fetch input, then fetch CLOSEST-FIRST instead of authoring order — so
        // a nearby ingredient isn't skipped to walk to a far one first. _inputsToFetch is rebuilt in
        // the same order so Complete's retry index-tracking still lines up with the walk order.
        var srcs = new List<(Item item, int perRound, int toFetch, Tile tile, ItemStack stack)>();
        foreach (var (item, perRound) in _inputsToFetch) {
            int toFetch = perRound * roundsRemaining - animal.inv.Quantity(item);
            // Already carrying enough for the (possibly carry-capped) round count — no fetch needed.
            // Skipping keeps _inputsToFetch (rebuilt from srcs below) and the FetchObjectives aligned,
            // and avoids failing the craft over an input whose only remaining copies are in our pack.
            if (toFetch <= 0) { continue; }
            (Path itemPath, ItemStack stack) = animal.nav.FindPathItemStack(item);
            if (itemPath == null) { return false; }
            srcs.Add((item, perRound, toFetch, itemPath.tile, stack));
        }
        var order = NearestFetchOrder(animal.x, animal.y, srcs.ConvertAll(s => s.tile));
        var reordered = new List<(Item item, int perRound)>(order.Count);
        foreach (int idx in order) reordered.Add((srcs[idx].item, srcs[idx].perRound));
        _inputsToFetch = reordered;
        // Prepend in reverse so order[0]'s fetch ends up front-most (ahead of Go/Work/Drop).
        for (int k = order.Count - 1; k >= 0; k--) {
            var s = srcs[order[k]];
            ReserveStack(s.stack, s.toFetch);
            objectives.AddFirst(new FetchObjective(this, new ItemQuantity(s.item, s.toFetch), s.tile, softFetch: true, sourceInv: s.stack.inv));
        }

        return true;
    }

    // Largest round count in [1, cap] whose still-to-fetch inputs (incl. committed fuel) fit the
    // animal's current pack, packing like Inventory.AddItem. 0 when even one round can't fit as-is
    // — the pack is too cluttered to carry the recipe (ChooseTask's drop trigger clears it). Fit is
    // monotonic in rounds, so scan from cap down and take the first that fits (cap fits on the first
    // pass in the common uncluttered case).
    private int MaxCarryableRounds(int cap){
        for (int r = cap; r >= 1; r--){
            var adds = new List<(Item, int)>(_inputsToFetch.Count);
            foreach (var (item, perRound) in _inputsToFetch)
                adds.Add((item, perRound * r - animal.inv.Quantity(item)));
            if (animal.inv.EmptyStacksToAbsorb(adds) <= animal.inv.CountEmptyStacks())
                return r;
        }
        return 0;
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
