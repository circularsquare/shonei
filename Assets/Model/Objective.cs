using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Base class for the individual steps a Task decomposes into. Tasks enqueue objectives in
// Initialize(), and the animal runs them sequentially via Task.StartNextObjective().
// Concrete subclasses live in Assets/Model/Objectives/.
public abstract class Objective {
    protected Task task;
    protected Animal animal;
    public Objective(Task task){
        this.task = task;
        this.animal = task.animal;
    }
    public abstract void Start();
    public virtual void OnArrival(){} // default do nothing, need to implement
    public void Complete() {
        task.Complete();
    }
    public void Fail(){
        task.Fail();
    }
    public virtual string GetObjectiveName() {
        return this.GetType().Name.Replace("Objective", "");
    }
    // Body pose the animal should strike while this objective is current, or null for the
    // default state-driven animation (idle/walk/eep). Mapped to an Animator int by
    // AnimationController.UpdateState(), which runs on animal-state change, nav locomotion,
    // and task selection (AnimalStateManager.HandleIdle) — NOT every frame. So an override is
    // applied at those moments; it stays until the next such event re-reads the current
    // objective. New objectives normally land on a state change (Moving→Working), which fires
    // the refresh; the HandleIdle hook covers the case where a task parks the animal straight
    // into a stationary pose before animal.task is even assigned.
    public virtual string PoseOverride => null;
    // Facing-view this objective forces ("back"/"front"), or null for nav/state-driven facing.
    // Mirrors PoseOverride: applied via ViewNameToFacing at the same UpdateState() moments.
    public virtual string ViewOverride => null;
    public override string ToString() {return GetObjectiveName();}
}
