// Tended-processor labour (cauldron): walk to the building and work its locked-in batch.
// AnimalStateManager.HandleWorking ticks the processor's `progress` while it's Working; on
// reaching `duration` it auto-taps the batch and calls Complete (registering the haul-out).
// Only fires while the processor is Working — the WorkProcessor order's isActive gate.
//
// The task reserves nothing on the processor, so a deconstruct mid-walk won't auto-fail it —
// hence the building.go == null liveness recheck in HandleWorking's WorkProcessor branch.
public class WorkProcessorTask : Task {
    public readonly Building building;
    public int ticksWorked; // capped at MaxWorkStintTicks so the worker re-evaluates needs

    public WorkProcessorTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }

    public override bool Initialize() {
        Processor proc = building?.processor;
        if (proc == null || proc.state != Processor.State.Working) return false;
        Path p = animal.nav.PathTo(building.workNode);
        if (!animal.nav.WithinWorkRange(p)) return false;
        objectives.AddLast(new GoObjective(this, building.workNode));
        objectives.AddLast(new WorkProcessorObjective(this));
        return true;
    }

    // Registers haul-out orders so the finished batch is carried to a liquid tank (mirrors
    // TapProcessorTask). RegisterStorageEvictionHaul dedups + idles until a tank exists.
    public static void RegisterHaulOut(Processor proc) {
        var wom = WorkOrderManager.instance;
        if (wom == null) return;
        foreach (ItemStack s in proc.output.itemStacks)
            if (s.item != null && s.quantity > 0)
                wom.RegisterStorageEvictionHaul(s);
    }
}
