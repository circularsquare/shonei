using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class AnimationController : MonoBehaviour {
    private Animal animal;
    private Animator animator;

    // Per-part clothing overlays — assign in prefab inspector.
    // Each entry maps a body-part name (e.g. "body", "arm", "foot") to its
    // clothing SpriteRenderer child.  Sprites are loaded from
    // Resources/Sprites/Animals/Clothing/{item}/{partName}.png
    [System.Serializable]
    public class PartClothing {
        public string partName;            // "body", "arm", "foot"
        public SpriteRenderer renderer;    // assigned on prefab
        [HideInInspector] public Sprite sprite;
    }
    public PartClothing[] clothingParts;

    private Item cachedClothingItem;  // tracks equipped item so we only reload on change

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

    private void UpdateClothingOverlay() {
        if (clothingParts == null || clothingParts.Length == 0) return;

        Item equipped = animal.clothingSlotInv?.itemStacks[0]?.item;
        if (equipped == null) {
            foreach (var part in clothingParts)
                if (part.renderer != null) part.renderer.enabled = false;
            cachedClothingItem = null;
            return;
        }

        // Reload sprites if clothing item changed
        if (equipped != cachedClothingItem) {
            cachedClothingItem = equipped;
            string basePath = "Sprites/Animals/Clothing/" + equipped.name;
            foreach (var part in clothingParts) {
                part.sprite = Resources.Load<Sprite>(basePath + "/" + part.partName);
            }
        }

        foreach (var part in clothingParts) {
            if (part.renderer == null) continue;
            if (part.sprite != null) {
                part.renderer.enabled = true;
                part.renderer.sprite = part.sprite;
            } else {
                part.renderer.enabled = false;
            }
        }
    }
}
