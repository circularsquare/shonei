using UnityEngine;

// Parks a worker at a TENDED processor (cauldron) to labour on its current batch. Mirrors
// WorkObjective, but the inputs live in the processor's buffer (not the animal's inventory), so
// there's no inv check. AnimalStateManager.HandleWorking ticks the processor's progress while
// state==Working and calls task.Complete() (after auto-tapping) when it reaches duration.
public class WorkProcessorObjective : Objective {
    public WorkProcessorObjective(Task task) : base(task) { }

    public override void Start(){
        if (task is WorkProcessorTask wpt && wpt.building != null) {
            // Position-based at-spot check (mirrors WorkObjective) — workNode may be an off-grid
            // waypoint whose nearest tile rounds away from the work tile.
            Node target = wpt.building.workNode;
            bool atSpot = target != null
                ? (Mathf.Abs(animal.x - target.wx) < 0.5f && Mathf.Abs(animal.y - target.wy) < 0.5f)
                : animal.TileHere() == wpt.building.workTile;
            if (!atSpot) {
                Debug.LogError($"{animal.aName} WorkProcessorObjective.Start: not at {wpt.building.structType.name} workspot, animal at ({animal.x},{animal.y})");
                Fail(); return;
            }
            // Register as the active worker so the building drives its work-state visuals (the
            // cauldron's brew fill) and the craft-gated fire light. Validated on read — no clear needed.
            wpt.building.workingAnimal = animal;
            animal.recipe = wpt.building.processor?.recipe;
        }
        animal.state = Animal.AnimalState.Working;
    }

    // The cauldron declares workView:"back" so the brewer turns to face the pot while working.
    public override string ViewOverride =>
        (task is WorkProcessorTask wpt) ? wpt.building?.structType.workView : null;
    public override string PoseOverride =>
        (task is WorkProcessorTask wpt) ? wpt.building?.structType.workPose : null;
}
