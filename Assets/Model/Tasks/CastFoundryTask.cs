// Pours a foundry's castable molten into bars, then registers eviction hauls so the bars get
// carried to storage. Registered as a standing CastFoundry order, active while any molten holds at
// least one bar's worth. Mirrors TapProcessorTask: no reservation on the foundry, so the building.go
// liveness recheck in Complete guards against a deconstruct mid-walk.
public class CastFoundryTask : Task {
    private readonly Foundry foundry;

    public CastFoundryTask(Animal animal, Building building) : base(animal) {
        this.foundry = building as Foundry;
    }

    public override bool Initialize() {
        if (foundry == null || !foundry.HasCastableMolten()) return false;
        Path standPath = animal.nav.PathToOrAdjacent(foundry.tile);
        if (!animal.nav.WithinRadius(standPath, MediumFindRadius)) return false;
        objectives.AddLast(new GoObjective(this, standPath.tile));
        return true;
    }

    public override void Complete() {
        if (foundry != null && foundry.go != null && foundry.HasCastableMolten()) {
            foundry.CastAll();
            var wom = WorkOrderManager.instance;
            if (wom != null)
                foreach (ItemStack s in foundry.output.itemStacks)
                    if (s.item != null && s.quantity > 0)
                        wom.RegisterStorageEvictionHaul(s);
        }
        base.Complete();
    }
}
