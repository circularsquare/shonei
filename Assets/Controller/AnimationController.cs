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

    // Assign in prefab inspector — the ChatBubble child's SpriteRenderer.
    // Shown while the animal is actively chatting; counter-flipped so it stays world-upright.
    public SpriteRenderer chatBubble;

    private Item cachedClothingItem;  // tracks equipped item so we only reload on change

    void Start() {
        animator = GetComponent<Animator>();
        animal = GetComponent<Animal>();
        if (chatBubble != null) chatBubble.enabled = false;
    }

    public void UpdateState() {
        if (animator == null) return; // AnimationController.Start() not yet called

        int stateInt;
        if (animal.state == Animal.AnimalState.Idle){ stateInt = 0; }
        else if (animal.state == Animal.AnimalState.Moving){
            // state=Moving doesn't mean the animal is actually walking — they could be
            // parked at an elevator boarding tile waiting for a ride, or riding the
            // platform. Nav.IsLocomoting tracks "actually translating via locomotion"
            // and we use the idle animation otherwise.
            stateInt = (animal.nav != null && animal.nav.IsLocomoting) ? 1 : 0;
        }
        else if (animal.state == Animal.AnimalState.Eeping){ stateInt = 2; }
        else if (animal.IsMoving()){ stateInt = 1; }
        else { stateInt = 0; }

        // Pose override layer: current Objective can request a body pose (e.g. "sit") that
        // wins over the state-driven animation. Pose is data-driven — see StructType.leisurePose,
        // StructType.workPose, and Objective.PoseOverride. Self-clears on objective transition.
        //
        // Special case: "walk" reuses the existing walk clip (state=1) instead of needing a
        // new pose layer in the Animator. Used by the mouse-wheel runner so the mouse's legs
        // cycle while producing power, without authoring a duplicate "walk-while-working" clip.
        string poseOverride = animal.task?.currentObjective?.PoseOverride;
        int poseInt;
        if (poseOverride == "walk") {
            stateInt = 1;
            poseInt = 0;
        } else {
            poseInt = PoseToInt(poseOverride);
        }
        animator.SetInteger("state", stateInt);
        animator.SetInteger("pose", poseInt);

        UpdateClothingOverlay();
        UpdateChatBubble();
    }

    // Maps a pose name (from JSON / Objective.PoseOverride) to the `pose` Animator int.
    // Add a case here when wiring a new pose clip in AnimControllerMouse.controller.
    // "walk" is handled in UpdateState (routed to state=1, not a real pose layer).
    private static int PoseToInt(string pose) {
        if (string.IsNullOrEmpty(pose)) return 0;
        switch (pose) {
            case "sit": return 1;
            default:
                Debug.LogError($"AnimationController.PoseToInt: unknown pose '{pose}' — add a case here.");
                return 0;
        }
    }

    // LateUpdate runs after Animal.Update() sets the root localScale for facing direction,
    // so we can correctly counter-flip the bubble here.
    void LateUpdate() {
        if (chatBubble == null || !chatBubble.enabled) return;
        // The root flips by setting localScale.x to ±1.  Setting the bubble's localScale.x
        // to the same value cancels it out (−1 × −1 = 1 world scale), keeping the sprite upright.
        float parentFlip = animal.go.transform.localScale.x;
        chatBubble.transform.localScale = new Vector3(parentFlip, 1, 1);
    }

    private void UpdateChatBubble() {
        if (chatBubble == null) return;
        bool chatting = animal.state == Animal.AnimalState.Leisuring
                     && animal.task?.currentObjective is ChatObjective co
                     && co.partner.state == Animal.AnimalState.Leisuring
                     && co.partner.task?.currentObjective is ChatObjective;
        // Also show bubble when socializing at a shared leisure building (e.g. fireplace)
        bool fireplaceChat = !chatting
                     && animal.state == Animal.AnimalState.Leisuring
                     && animal.task?.currentObjective is LeisureObjective lo
                     && lo.isSocializing;
        chatBubble.enabled = chatting || fireplaceChat;
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
