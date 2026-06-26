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
    // Resources/Sprites/Animals/Clothing/{item}/{partName}.png — or
    // {partName}_back.png when the mouse is back-facing (no back variant → part hidden).
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

    // Assign in prefab inspector — the Hat child renderer under Head. Driven by hatSlotInv,
    // separate from the clothing overlay (different slot, head-anchored). See UpdateHatOverlay.
    public SpriteRenderer hatRenderer;

    private Item cachedClothingItem;  // tracks equipped item so we only reload on change
    private Animal.FacingView cachedClothingView = Animal.FacingView.Side; // reload also on view flip
    private Item cachedHatItem;       // same caching for the hat overlay
    private Animal.FacingView cachedHatView = Animal.FacingView.Side;
    private Sprite cachedHatSprite;
    private Vector3 hatBasePos;        // prefab-authored Hat localPosition (side/front view), cached in Start
    // Back-facing hats sit 1px (1/16 unit) left of their side-view spot — the back-of-head art
    // is centred differently. In local space so it mirrors correctly with the mouse's facing flip.
    const float HatBackXShift = -0.0625f;
    private bool hasBackParam;        // true if the Animator declares the "back" int (wired later)

    static readonly int FurColorId = Shader.PropertyToID("_FurColor");

    // Applies this mouse's fur tint by setting the _FurColor per-renderer property on every
    // body SpriteRenderer — body, head, legs, arm, tail, and their back/front/eep variants,
    // which are sprite-swaps on these same renderers, so all are covered by one set. The
    // Custom/Sprite shader recolors only the gray fur shades, leaving eyes and pink paws/ears
    // untouched. Clothing renderers and the chat bubble are excluded so they keep their own
    // colors. _FurColor is [PerRendererData] (MPB-set), so SRP batching is preserved.
    // Called once from Animal.Start after rngSeed is finalized; safe regardless of Start
    // ordering since clothingParts/chatBubble are serialized and child renderers always exist.
    public void ApplyFurColor(Color main) {
        var skip = new HashSet<SpriteRenderer>();
        if (clothingParts != null)
            foreach (var part in clothingParts)
                if (part.renderer != null) skip.Add(part.renderer);
        if (chatBubble != null) skip.Add(chatBubble);
        if (hatRenderer != null) skip.Add(hatRenderer); // hat keeps its own colors, like clothing

        var mpb = new MaterialPropertyBlock();
        foreach (var sr in GetComponentsInChildren<SpriteRenderer>(true)) {
            if (skip.Contains(sr)) continue;
            sr.GetPropertyBlock(mpb);
            mpb.SetColor(FurColorId, main);
            sr.SetPropertyBlock(mpb);
        }
    }

    void Start() {
        animator = GetComponent<Animator>();
        animal = GetComponent<Animal>();
        if (chatBubble != null) chatBubble.enabled = false;
        if (hatRenderer != null) hatBasePos = hatRenderer.transform.localPosition;
        // Probe for the "back" Animator param so we don't spam warnings before it's authored.
        if (animator != null)
            foreach (var param in animator.parameters)
                if (param.type == AnimatorControllerParameterType.Int && param.name == "back") { hasBackParam = true; break; }
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

        // Facing-view: an objective override (e.g. crucible workView) wins; otherwise the
        // edge-implied view while actually locomoting (a straight-ladder climb); else Side.
        // The two sources never co-occur — a climb runs under GoObjective (null ViewOverride),
        // and work is stationary (not locomoting) — so this is a clean precedence, not a race.
        string viewOverride = animal.task?.currentObjective?.ViewOverride;
        Animal.FacingView view;
        if (!string.IsNullOrEmpty(viewOverride)) view = ViewNameToFacing(viewOverride);
        else if (animal.nav != null && animal.nav.IsLocomoting) view = animal.nav.CurrentEdgeView;
        else view = Animal.FacingView.Side;

        if (hasBackParam) animator.SetInteger("back", view == Animal.FacingView.Back ? 1 : 0);

        UpdateClothingOverlay(view);
        UpdateHatOverlay(view);
        UpdateChatBubble();
    }

    // Maps a view name (from JSON workView / Objective.ViewOverride) to a FacingView.
    // Parallels PoseToInt. Front is accepted for forward-compat but has no art yet.
    private static Animal.FacingView ViewNameToFacing(string view) {
        switch (view) {
            case "back":  return Animal.FacingView.Back;
            case "front": return Animal.FacingView.Front;
            default:
                Debug.LogError($"AnimationController.ViewNameToFacing: unknown view '{view}' — add a case here.");
                return Animal.FacingView.Side;
        }
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

    private void UpdateClothingOverlay(Animal.FacingView view) {
        if (clothingParts == null || clothingParts.Length == 0) return;

        Item equipped = animal.clothingSlotInv?.itemStacks[0]?.item;
        if (equipped == null) {
            foreach (var part in clothingParts)
                if (part.renderer != null) part.renderer.enabled = false;
            cachedClothingItem = null;
            return;
        }

        // Reload sprites if the equipped item OR the facing-view changed. Back-facing loads
        // the {part}_back variant; a part with no back variant resolves to null and hides
        // (matches the body-part arm-hide convention) rather than showing its front sprite.
        if (equipped != cachedClothingItem || view != cachedClothingView) {
            cachedClothingItem = equipped;
            cachedClothingView = view;
            string basePath = "Sprites/Animals/Clothing/" + equipped.name;
            string suffix = view == Animal.FacingView.Back ? "_back" : "";
            foreach (var part in clothingParts) {
                part.sprite = Resources.Load<Sprite>(basePath + "/" + part.partName + suffix);
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

    // Draws the mouse's worn hat on the head from hatSlotInv. Hats live flat at
    // Sprites/Animals/Clothing/hats/{despaced item name}[_back] (one sprite per hat) — the
    // despacing matches Db.LoadItemIcons ("cloth hat" → "clothhat"). Back-facing loads the
    // {name}_back variant; with no back art the hat hides while back-facing (same graceful
    // convention as clothing parts). Reloads only on equipped-item or view change.
    private void UpdateHatOverlay(Animal.FacingView view) {
        if (hatRenderer == null) return;

        Item equipped = animal.hatSlotInv?.itemStacks[0]?.item;
        if (equipped == null) {
            hatRenderer.enabled = false;
            cachedHatItem = null;
            return;
        }

        if (equipped != cachedHatItem || view != cachedHatView) {
            cachedHatItem = equipped;
            cachedHatView = view;
            string basePath = "Sprites/Animals/Clothing/hats/" + equipped.name.Replace(" ", "");
            // Back-facing prefers a {name}_back variant; unlike clothing we fall BACK to the
            // front sprite when none exists (rather than hiding), so a mouse's profession hat
            // stays visible even while it works at a back-view station — the whole point is
            // at-a-glance identification. A hat with no art at all simply hides.
            Sprite s = view == Animal.FacingView.Back ? Resources.Load<Sprite>(basePath + "_back") : null;
            cachedHatSprite = s ?? Resources.Load<Sprite>(basePath);
            // Nudge the back-facing hat off its side-view spot (back-of-head art is centred
            // differently). Front/side use the prefab-authored position unchanged.
            hatRenderer.transform.localPosition = view == Animal.FacingView.Back
                ? hatBasePos + new Vector3(HatBackXShift, 0f, 0f)
                : hatBasePos;
        }

        if (cachedHatSprite != null) {
            hatRenderer.enabled = true;
            hatRenderer.sprite = cachedHatSprite;
        } else {
            hatRenderer.enabled = false;
        }
    }
}
