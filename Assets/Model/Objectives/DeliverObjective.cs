using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class DeliverObjective : Objective { // navigates and drops off item
    private ItemQuantity iq;
    private Tile destination;
    public DeliverObjective(Task task, ItemQuantity iq, Tile destination) : base(task) {
        this.iq = iq;
        this.destination = destination;
    }
    public override void Start(){
        Path path = animal.nav.PathTo(destination);
        if (path != null){
            animal.nav.Navigate(path);
            animal.state = Animal.AnimalState.Moving;
        } else {Fail();}
    }
    public override void OnArrival(){
        if (animal.inv.Quantity(iq.item) == 0){ Debug.Log($"{animal.aName} DeliverObjective: missing {iq.item.name} to deliver to ({destination.x},{destination.y})"); Fail(); return; }
        int moved = animal.DropItem(iq);  // drops the amount you have up to iq.quantity. if have less, just drops that.
        if (moved < iq.quantity)
            Debug.Log($"{animal.aName} ({animal.job.name}) [{task}] at ({(int)animal.x},{(int)animal.y}): partial/failed drop of {iq.item.name} — moved {moved}/{iq.quantity} to ({destination.x},{destination.y})");
        Complete();
    }
}
