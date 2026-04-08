using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Optional component of a Building that represents a workstation (crafting station).
/// Owns the player-adjustable worker slot limit. Non-null only when structType.isWorkstation.
/// WOM reads workstation.workerLimit when registering craft orders.
/// </summary>
public class Workstation {
    /// <summary>Max workers from StructType.capacity.</summary>
    public int capacity;

    /// <summary>
    /// Player-adjustable worker limit. Defaults to capacity (all slots open).
    /// Persisted via StructureSaveData.workOrderEffectiveCapacity.
    /// Use WorkOrderManager.SetWorkstationCapacity() to change at runtime.
    /// </summary>
    public int workerLimit;

    /// <summary>
    /// Completed craft rounds at this workstation. Compared against structType.depleteAt
    /// to trigger building depletion. Persisted via StructureSaveData.uses.
    /// </summary>
    public int uses = 0;

    public Workstation(int capacity) {
        this.capacity = capacity;
        this.workerLimit = capacity;
    }
}

/// <summary>
/// Optional component of a Building that manages an internal consumable-resource inventory.
/// Owns inv, fuelItem, capacity, and burn rate. Non-null only when structType.hasFuelInv.
/// Works for any drainable resource (fuel, water, etc.).
/// LightSource consumes via Burn(). WOM registers a standing SupplyBuilding order via building.reservoir.
/// Supply is triggered when quantity falls below half of capacity.
/// </summary>
public class Reservoir {
    /// <summary>The leaf or group item this reservoir accepts (e.g. "wood", "water").</summary>
    public Item fuelItem;
    /// <summary>Max stack size in fen.</summary>
    public int capacity;
    /// <summary>Liang/day consumed; LightSource converts to fen/s at runtime.</summary>
    public float burnRate;
    /// <summary>Internal inventory: 1 stack, not tied to a tile.</summary>
    public Inventory inv;

    public Reservoir(Item fuelItem, int capacity, float burnRate, int buildingX, int buildingY, string buildingName) {
        this.fuelItem = fuelItem;
        this.capacity = capacity;
        this.burnRate = burnRate;
        inv = new Inventory(1, capacity, Inventory.InvType.Reservoir, buildingX, buildingY);
        inv.displayName = buildingName + "_fuel";
    }

    /// <summary>Current quantity for the configured item (checks all leaf stacks).</summary>
    public int Quantity() => inv.Quantity(fuelItem);

    /// <summary>True when level is below half of capacity — triggers a WOM supply order.</summary>
    public bool NeedsSupply() => inv.Quantity(fuelItem) < capacity / 2;

    /// <summary>True when level is above zero.</summary>
    public bool HasFuel() => inv.Quantity(fuelItem) > 0;

    /// <summary>
    /// Consumes the resource over time. Call from Update(). Returns the amount actually consumed (fen).
    /// Accumulates fractional fen across frames so sub-fen burn rates work correctly.
    /// </summary>
    public int Burn(float deltaTime, ref float accumulator) {
        float fenPerSecond = burnRate * 100f / World.ticksInDay;
        accumulator += fenPerSecond * deltaTime;
        if (accumulator < 1f) return 0;

        int toConsume = Mathf.FloorToInt(accumulator);
        int remaining = toConsume;
        foreach (ItemStack stack in inv.itemStacks) {
            if (stack.item == null || stack.quantity == 0 || remaining <= 0) continue;
            int fromThisStack = Mathf.Min(remaining, stack.quantity);
            inv.Produce(stack.item, -fromThisStack);
            remaining -= fromThisStack;
        }
        int consumed = toConsume - remaining;
        accumulator -= consumed > 0 ? consumed : toConsume;
        if (accumulator < 0f) accumulator = 0f;
        return consumed;
    }

    /// <summary>
    /// Drops remaining contents onto the floor at the given tile. Used during building deconstruct
    /// so items aren't silently lost.
    /// </summary>
    public void DropToFloor(Tile here) {
        if (inv.IsEmpty() || here == null) return;
        foreach (ItemStack stack in inv.itemStacks) {
            if (stack.item == null || stack.quantity == 0) continue;
            int qty = stack.quantity;
            inv.Produce(stack.item, -qty);
            World.instance.ProduceAtTile(stack.item, qty, here);
        }
    }

    public void Destroy() {
        inv.Destroy();
    }
}

public class Building : Structure {
    // When true, all work orders for this building are suppressed. Player-togglable via UI.
    // Distinct from IsActive() which checks runtime conditions (e.g. pump has water).
    public bool disabled = false;

    // Non-null only for workstation buildings. Owns the player-adjustable worker slot limit.
    public Workstation workstation { get; private set; }
    public Inventory storage { get; private set; }
    // Non-null only for buildings with a consumable resource reservoir (torch, furnace, fountain, etc.).
    public Reservoir reservoir { get; private set; }

    public Building(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored){
        go.name = "building_" + structType.name;

        if (st.isWorkstation)
            workstation = new Workstation(Mathf.Max(1, st.capacity));

        if (structType.isStorage){
            Tile sTile = World.instance.GetTileAt(
                x + (mirrored ? (st.nx - 1 - st.storageTileX) : st.storageTileX),
                y + st.storageTileY);
            var invType = structType.name == "market" ? Inventory.InvType.Market : Inventory.InvType.Storage;
            storage = new Inventory(structType.nStacks, structType.storageStackSize, invType, sTile.x, sTile.y, isLiquidStorage: structType.liquidStorage);
            storage.displayName = structType.name;
            // Floor items stay on the floor — storage is separate (building.storage).
        }

        if (st.hasFuelInv) {
            reservoir = new Reservoir(st.fuelItem, st.fuelCapacity, st.fuelBurnRate, x, y, st.name);
            if (st.isLightSource) {
                var ls = go.AddComponent<LightSource>();
                ls.baseIntensity = st.lightIntensity;
                ls.reservoir = reservoir;
                ls.activeStartHour = st.activeStartHour;
                ls.activeEndHour   = st.activeEndHour;
                // Start unlit — Update() will set isLit correctly on the first frame
                // once fuel state is known. Avoids a one-frame flicker on placement/load.
                ls.isLit = false;
            }
        }
    }

    public override void OnPlaced() {
        WorkOrderManager.instance?.RegisterOrdersFor(this);
    }

    public override void Destroy() {
        if (workstation != null)
            WorkOrderManager.instance?.RemoveWorkstationOrders(this);
        if (structType.isStorage && storage != null) {
            if (!storage.IsEmpty() && !WorldController.isClearing)
                Debug.LogError($"Destroying building storage with items in it at ({x},{y})!");
            storage.Destroy();
        }
        if (reservoir != null) {
            WorkOrderManager.instance?.RemoveFuelSupplyOrders(this);
            if (!WorldController.isClearing)
                reservoir.DropToFloor(tile);
            reservoir.Destroy();
        }
        base.Destroy();
    }
}
