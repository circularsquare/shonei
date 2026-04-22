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
    // default state-driven animation (idle/walk/eep). Read by AnimationController each frame
    // and mapped to an Animator int. Self-clears on objective transition — no explicit reset.
    public virtual string PoseOverride => null;
    public override string ToString() {return GetObjectiveName();}
}
