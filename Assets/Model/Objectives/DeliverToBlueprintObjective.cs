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
            animal.inv.MoveItemTo(blueprint.inv, iq.item, Math.Min(animal.inv.Quantity(iq.item), needed));
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
