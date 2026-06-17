using UnityEngine;

// Parks the animal in Working state to water the soil tile below a plant.
// AnimalStateManager.HandleWorking ticks progress and, on completion, pours the
// carried water item into the soil (see WaterPlantTask.PourWater) and calls Complete().
public class WaterObjective : Objective {
    private readonly Plant plant;
    public WaterObjective(Task task, Plant plant) : base(task) {
        this.plant = plant;
    }
    public override void Start(){
        if (plant == null || plant.tile == null) { Fail(); return; }
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
        // AnimalStateManager.HandleWorking pours + completes once workProgress reaches WaterPlantTask.WaterTime.
    }
}
