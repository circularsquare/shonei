using UnityEngine;

// A hauler draws water from a well: walk to the wellhead, lower the bucket to the water surface,
// wait, raise it with ~5 liang, then haul the water off to storage. Dispatched through the craft
// layer (Animal.ChooseCraftTask) like the pump — so it competes for water demand the same way — but
// runs a bespoke lowering-bucket draw instead of the fixed-workload craft loop, so the time scales
// with the water depth. The draw itself (bucket motion + producing the water) is driven by the
// Well's per-tick state machine; this task just parks the hauler at the wellhead and hauls the result.
public class DrawWaterTask : Task {
    public readonly Well well;
    private readonly Recipe _recipe;

    public DrawWaterTask(Animal animal, Well well, Recipe recipe) : base(animal) {
        this.well = well;
        _recipe   = recipe;
    }

    public override bool Initialize() {
        if (well == null || well.go == null) return false;
        if (!well.HasDrawableWater) return false;          // needs a worthwhile buffer, not a trickle
        if (well.IsDrawing) return false;                 // someone's already drawing (capacity 1)
        Path p = animal.nav.PathTo(well.workNode);
        if (!animal.nav.WithinWorkRange(p)) return false;

        objectives.AddLast(new GoObjective(this, well.workNode));
        objectives.AddLast(new DrawWaterObjective(this, well, _recipe));
        foreach (ItemQuantity output in _recipe.outputs)
            objectives.AddLast(new DropObjective(this, output.item));
        return true;
    }
}
