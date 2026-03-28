using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;

public class AnimationController : MonoBehaviour {
    private Animal animal;
    private Animator animator;
    private enum AnimationState { Idle, Walking, Eeping }

    // Clothing overlay — assigned on the prefab
    public SpriteRenderer clothingRenderer;
    private Item cachedClothingItem;  // tracks what's equipped so we only reload on change
    private Sprite clothingIdle;
    private Sprite clothingWalk;
    private Sprite clothingEep;

    void Start() {
        animator = GetComponent<Animator>();
        animal = GetComponent<Animal>();
    }

    public void UpdateState() {
        if (animal.state == Animal.AnimalState.Idle){ animator.SetInteger("state", 0); }
        else if (animal.state == Animal.AnimalState.Moving){ animator.SetInteger("state", 1); }
        else if (animal.state == Animal.AnimalState.Eeping){ animator.SetInteger("state", 2); }
        else if (animal.IsMoving()){ animator.SetInteger("state", 1); }
        else { animator.SetInteger("state", 0); }

        UpdateClothingOverlay();
    }

    void LateUpdate() {
        // Sync clothing flipX every frame (body flipX changes during movement, not just on state change)
        if (clothingRenderer != null && clothingRenderer.enabled)
            clothingRenderer.flipX = animal.sr.flipX;
    }

    private void UpdateClothingOverlay() {
        if (clothingRenderer == null) return;

        Item equipped = animal.clothingSlotInv?.itemStacks[0]?.item;
        if (equipped == null) {
            clothingRenderer.enabled = false;
            cachedClothingItem = null;
            return;
        }

        // Reload sprites if clothing item changed
        if (equipped != cachedClothingItem) {
            cachedClothingItem = equipped;
            string path = "Sprites/Animals/Clothing/" + equipped.name;
            clothingIdle = Resources.Load<Sprite>(path + "/idle");
            clothingWalk = Resources.Load<Sprite>(path + "/walk");
            clothingEep  = Resources.Load<Sprite>(path + "/eep");
            if (clothingIdle == null)
                Debug.LogError($"Missing clothing sprite: {path}/idle");
        }

        clothingRenderer.enabled = true;
        clothingRenderer.flipX = animal.sr.flipX;

        if (animal.state == Animal.AnimalState.Eeping)
            clothingRenderer.sprite = clothingEep ?? clothingIdle;
        else if (animal.state == Animal.AnimalState.Moving || animal.IsMoving())
            clothingRenderer.sprite = clothingWalk ?? clothingIdle;
        else
            clothingRenderer.sprite = clothingIdle;
    }
}