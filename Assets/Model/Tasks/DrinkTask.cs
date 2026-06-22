// Leisure: a mouse walks to wherever rice wine is stored (a tank, the brewery's output
// bin, a floor stack — anywhere), drinks 1 liang on the spot, and gains a strong
// "alcohol" happiness grant. No equip, no carry — consumed in place.
//
// Decoupled from any building: wine is just an item. Spawned by Animal.TryPickLeisure
// whenever rice wine exists in the world — the same item-quantity trigger ReadBookTask
// uses. The wine stack is reserved up front so two thirsty mice can't claim the same liang.
public class DrinkTask : Task {
    public override bool IsWork => false;

    private const float DrinkGrant = 1.5f;  // strong "alcohol" leisure grant
    private const int DrinkQuantity = 100;  // 1 liang consumed per drink

    // Cached on first Db lookup. If absent (JSON misspelled), Initialize bails.
    private static Item riceWineItem;

    private Inventory wineInv;   // the inventory the reserved wine sits in; drained in Complete

    public DrinkTask(Animal animal) : base(animal) {}

    public override bool Initialize() {
        if (riceWineItem == null && !Db.itemByName.TryGetValue("rice wine", out riceWineItem)) return false;
        // "Don't consume" rice wine ⇒ no drinking it for leisure.
        if (InventoryController.instance != null && InventoryController.instance.IsConsumptionDisabled(riceWineItem)) return false;

        // Nearest reachable wine stack. FindPathItemStack is read-only (no reservation),
        // so re-check the unreserved amount before committing.
        (Path path, ItemStack stack) = animal.nav.FindPathItemStack(riceWineItem);
        if (path == null) return false;
        if (stack.quantity - stack.resAmount < DrinkQuantity) return false;

        int reserved = ReserveStack(stack, DrinkQuantity);
        if (reserved < DrinkQuantity) return false;
        wineInv = stack.inv;

        // Single objective: walk to the wine. Consumption + happiness fire in Complete().
        objectives.AddLast(new GoObjective(this, path.tile));
        return true;
    }

    public override void Complete() {
        // Drain 1 liang on the spot. Negative Produce subtracts (and decrements the global
        // inventory); base.Cleanup releases the stack reservation.
        wineInv.Produce(riceWineItem, -DrinkQuantity);
        animal.happiness.NoteLeisure("alcohol", DrinkGrant);
        base.Complete();
    }
}
