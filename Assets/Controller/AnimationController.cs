using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;

public class AnimationController : MonoBehaviour {
    private Animal animal;
    private Animator animator;
    private enum AnimationState { Idle, Walking, Eeping }

    void Start(){
        animator = GetComponent<Animator>();
        animal = GetComponent<Animal>();
    }

    public void UpdateState(){
        if (animal.state == Animal.AnimalState.Idle){ animator.SetInteger("state", 0); }
        else if (animal.state == Animal.AnimalState.Moving){ animator.SetInteger("state", 1); }
        else if (animal.state == Animal.AnimalState.Eeping){ animator.SetInteger("state", 2); }
        else if (animal.IsMoving()){ animator.SetInteger("state", 1); }
        else { animator.SetInteger("state", 0); }
    }
}