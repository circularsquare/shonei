using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class DeliverToBlueprintObjective : Objective { // always queued after GoObjective; Start() runs immediately once the animal is in position
    private ItemQuantity iq;
    private Blueprint blueprint;
    public DeliverToBlueprintObjective(Task task, ItemQuantity iq, Blueprint blueprint) : base(task) {
        this.iq = iq;
        this.blueprint = blueprint;
    }
    public override void Start(){
        if (blueprint == null || blueprint.cancelled) { Fail(); return; }
        if (animal.inv.Quantity(iq.item) > 0) {
            int needed = 0;
            // Use MatchesItem so a leaf iq.item (e.g. "pine") matches a group cost.item (e.g. "wood")
            // that hasn't been locked yet, as well as the exact match once it is locked.
            foreach (var cost in blueprint.costs)
                if (Inventory.MatchesItem(iq.item, cost.item)) { needed = cost.quantity - blueprint.inv.Quantity(cost.item); break; }
            int requested = Math.Min(animal.inv.Quantity(iq.item), needed);
            int moved = animal.inv.MoveItemTo(blueprint.inv, iq.item, requested);
            // Canary for slot-routing bugs: if needed > 0 the matching cost slot should have had
            // capacity, and slotConstraints should have routed the item there. A 0 move means
            // either the slot is the wrong size for its cost or the constraint filter rejected
            // the item — both indicate the blueprint's inventory layout is broken.
            if (requested > 0 && moved == 0)
                Debug.LogWarning($"DeliverToBlueprintObjective: {animal.aName} delivered 0 of {iq.item.name} to '{blueprint.structType.name}' blueprint at ({blueprint.x},{blueprint.y}) despite needed={needed} and carrying={animal.inv.Quantity(iq.item)} — slot routing may be broken.");
            blueprint.LockGroupCostsAfterDelivery();
            if (blueprint.IsFullyDelivered()) {
                blueprint.state = Blueprint.BlueprintState.Constructing;
                WorkOrderManager.instance?.PromoteToConstruct(blueprint);
            }
            Complete();
        } else {
            Debug.Log($"{animal.aName} could not deliver {iq.item.name} to blueprint at ({blueprint.x},{blueprint.y})");
            Fail();
        }
    }
}
