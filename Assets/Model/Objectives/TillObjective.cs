// Parks the animal in Working state to till the soil tile below it. AnimalStateManager.HandleWorking
// ticks progress and, on completion, flips the tile to tilled (see TillSoilTask.DoTill) and calls
// task.Complete() once workProgress reaches TillSoilTask.TillTime.
public class TillObjective : Objective {
    public TillObjective(Task task) : base(task) {}
    public override void Start(){
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Working;
    }
    // Back to camera while tilling — the shared farm-work visual cue (see HarvestObjective).
    public override string ViewOverride => "back";
}
