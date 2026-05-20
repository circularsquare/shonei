// Cook task: tap a Processor whose fermentation has finished (Ready state). The cook
// walks to the building and, on arrival, converts the batch — Processor.Tap() drains
// the inputBuffer and produces the output (rice wine), entering the Tapped state.
//
// The tap reserves nothing on the processor, so a deconstruct mid-walk would not
// auto-fail this task — hence the building.go == null liveness recheck in Complete.
public class TapProcessorTask : Task {
    private readonly Building building;

    public TapProcessorTask(Animal animal, Building building) : base(animal) {
        this.building = building;
    }

    public override bool Initialize() {
        Processor proc = building?.processor;
        if (proc == null || proc.state != Processor.State.Ready) return false;
        Path standPath = animal.nav.PathToOrAdjacent(building.tile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        objectives.AddLast(new GoObjective(this, standPath.tile));
        return true;
    }

    public override void Complete() {
        // Liveness recheck — building.go == null is the WOM-audit idiom for a deconstructed
        // building. Without it, tapping a destroyed processor would touch dead inventories.
        if (building != null && building.go != null) {
            Processor proc = building.processor;
            if (proc != null && proc.state == Processor.State.Ready) {
                proc.Tap();
                // Register haul orders so haulers carry the finished wine out to a liquid
                // tank. RegisterStorageEvictionHaul is mechanically "haul this Storage stack
                // to storage" (the order idles until a tank exists). When `output` empties,
                // Processor.Tick flips Tapped→Empty and the brewery can reload.
                var wom = WorkOrderManager.instance;
                if (wom != null)
                    foreach (ItemStack s in proc.output.itemStacks)
                        if (s.item != null && s.quantity > 0)
                            wom.RegisterStorageEvictionHaul(s);
            }
        }
        base.Complete();
    }
}
