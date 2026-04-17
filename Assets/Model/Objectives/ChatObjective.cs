using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// ChatObjective: handles the "stand and chat" phase of a ChatTask.
// All chat-specific tick logic lives in AnimalStateManager.HandleChatting().
public class ChatObjective : Objective {
    public Animal partner;
    public int duration;
    public ChatObjective(Task task, Animal partner, int duration) : base(task) {
        this.partner = partner;
        this.duration = duration;
    }
    public override void Start() {
        animal.workProgress = 0f;
        animal.state = Animal.AnimalState.Leisuring;
        // Face partner
        animal.facingRight = (partner.x > animal.x);
        if (animal.go != null) animal.go.transform.localScale = new Vector3(animal.facingRight ? 1 : -1, 1, 1);
        // If partner is already waiting in their ChatObjective, sync both timers
        if (partner.state == Animal.AnimalState.Leisuring
            && partner.task?.currentObjective is ChatObjective partnerObj) {
            partner.workProgress = 0f;
            animal.animationController?.UpdateState();
            partner.animationController?.UpdateState();
        }
        // AnimalStateManager.HandleChatting ticks workProgress and calls Complete() when done.
    }
}
