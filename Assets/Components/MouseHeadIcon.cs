using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Reusable mouse-head portrait widget. Attach to a UI Image GameObject and call
// Set(Animal) to display that mouse's head. Used in housing occupant lists, work-flag
// rosters, and anywhere a mouse needs to be identified at a glance. Hovering shows the
// mouse's name.
//
// Every mouse shares the one `mouse_head` sprite; per-mouse fur color is applied as a tint
// (see furMaterial below), matching the in-world mouse. Set() is animal-typed (not
// sprite-typed) so further per-mouse appearance — profession hats, etc. — can layer in here
// without touching any call site.
[RequireComponent(typeof(Image))]
public class MouseHeadIcon : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler {
    // Shared head sprite, loaded once. Point-filtered pixel art (see import settings).
    // A Resources asset ref — safe to hold statically across scene/domain reloads.
    static Sprite headSprite;

    // Shared material running Custom/MouseHeadUI: recolors the gray fur shades to the per-icon
    // Image.color (which the Canvas feeds the shader as vertex color), leaving eyes and pink
    // ears constant — same remap as the in-world mouse. One shared instance keeps all heads
    // batched (only the vertex color differs per icon). Resources ref → safe static.
    static Material furMaterial;
    static bool furMaterialTried;

    // Shared UI material that samples bilinear (Custom/UIBilinear) so the hat overlay smooths at
    // the non-integer UI scale without changing the Point world texture. Resources ref → safe static.
    static Material hatMaterial;
    static bool hatMaterialTried;

    Image image;
    Animal animal;

    // Runtime-created child Image overlaying the worn hat on the portrait (null until the first
    // hatted mouse). The portrait is a single Image, so the hat layers as a sibling overlay here
    // — no call site or prefab change needed (see class header). Hats keep their own colors (no
    // fur tint), and the overlay ignores raycasts so clicking the head still selects the mouse.
    Image hatImage;
    // Hat overlay sizing/placement, in units of the head cell. The head sprite (10px wide) displays
    // filling the cell, but the hat sprite is 16px wide — so to render the hat at the SAME art-pixel
    // scale as the head it must be 16/10 = 1.6× the cell (the per-art-pixel ratio, cell-size
    // independent). HatOffsetFrac then nudges it onto the head. Both tuned by eye against the portrait.
    const float HatScale = 1.6f;
    // Offset in HAT-SPRITE pixels (the 16px hat's own pixels — same unit as the in-world hat), so a
    // nudge reads in sprite terms regardless of HatScale. Converted to a cell-anchor fraction in
    // UpdateHatOverlay: one hat-sprite-px spans HatScale/16 of the cell (the hat renders 1.6× scale).
    static readonly Vector2 HatOffsetPx = new Vector2(0f, -2.5f);

    // Optional click callback (mirrors ItemIcon). If set, clicking the head fires it with
    // this mouse — e.g. the occupant list uses it to select the mouse in the InfoPanel.
    // If null, clicks are inert. Note: implementing IPointerClickHandler means the head
    // consumes clicks, so don't nest a click-less head inside a parent Button without
    // routing the parent's action through onClick too.
    public System.Action<Animal> onClick;

    void Awake() {
        image = GetComponent<Image>();
    }

    public void Set(Animal a) {
        // Lazy-init: callers may Set() before Awake runs (e.g. building into a
        // still-inactive container), where the cached Image isn't assigned yet.
        if (image == null) image = GetComponent<Image>();
        // The head sprite isn't square; preserve aspect so a square layout cell (e.g. the
        // 16×16 occupant-row slot) letterboxes it instead of stretching it vertically.
        image.preserveAspect = true;
        animal = a;
        if (animal == null) { gameObject.SetActive(false); return; }
        if (headSprite == null) headSprite = Resources.Load<Sprite>("Sprites/Animals/mouse_head");
        if (headSprite == null) {
            Debug.LogError("MouseHeadIcon: could not load Sprites/Animals/mouse_head");
            return;
        }
        image.sprite = headSprite;

        // Apply this mouse's fur tint. Lazy-load the shared material once; on failure we
        // leave the head untinted (default UI material) rather than multiply-tinting eyes/ears.
        if (!furMaterialTried) {
            furMaterialTried = true;
            furMaterial = Resources.Load<Material>("Materials/MouseHeadUI");
            if (furMaterial == null)
                Debug.LogError("MouseHeadIcon: could not load Materials/MouseHeadUI — fur tint disabled");
        }
        if (furMaterial != null) {
            image.material = furMaterial;
            image.color = Db.FurColorForSeed(animal.rngSeed);
        }

        UpdateHatOverlay();
        gameObject.SetActive(true);
    }

    // Shows the mouse's worn hat on the portrait, mirroring the in-world head overlay. Uses the
    // same worn-hat art (Sprites/Animals/Clothing/hats/{despaced name}) and keeps its own colors.
    // Lazily creates the overlay child on first use.
    void UpdateHatOverlay() {
        Item hat = animal?.hatSlotInv?.itemStacks[0]?.item;
        Sprite hatSprite = hat != null
            ? Resources.Load<Sprite>("Sprites/Animals/Clothing/hats/" + hat.name.Replace(" ", ""))
            : null;
        if (hatSprite == null) {
            if (hatImage != null) hatImage.gameObject.SetActive(false);
            return;
        }
        if (hatImage == null) {
            var go = new GameObject("HatOverlay", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            var rt = (RectTransform)go.transform;
            // Scale the overlay to HatScale× the cell (centered), then shift by HatOffsetFrac.
            // Anchor-based (with anchors past 0..1 to grow beyond the cell) so it stays cell-size
            // independent — no runtime rect measurement.
            float half = (HatScale - 1f) * 0.5f;
            Vector2 off = HatOffsetPx * (HatScale / 16f); // hat-sprite-px → fraction of the cell
            rt.anchorMin = new Vector2(-half + off.x, -half + off.y);
            rt.anchorMax = new Vector2(1f + half + off.x, 1f + half + off.y);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            hatImage = go.GetComponent<Image>();
            hatImage.raycastTarget = false;   // clicks pass through to the head
            hatImage.preserveAspect = true;
            // Bilinear UI material so the Point hat texture smooths in the UI (world stays crisp).
            if (!hatMaterialTried) {
                hatMaterialTried = true;
                hatMaterial = Resources.Load<Material>("Materials/UIBilinear");
                if (hatMaterial == null)
                    Debug.LogError("MouseHeadIcon: could not load Materials/UIBilinear — hat icon will not smooth");
            }
            if (hatMaterial != null) hatImage.material = hatMaterial;
        }
        hatImage.sprite = hatSprite;
        hatImage.color = Color.white;          // hats keep their own colors (no fur tint)
        hatImage.gameObject.SetActive(true);
    }

    public void OnPointerEnter(PointerEventData eventData) {
        if (animal == null) return;
        // "Yarrow (miner)" — append the job, but skip the "none" job (id 0) so an
        // unassigned mouse just reads as its name.
        string label = animal.aName;
        if (animal.job != null && animal.job.id != 0) label += " (" + animal.job.name + ")";
        TooltipSystem.Show(label, "");
    }

    public void OnPointerExit(PointerEventData eventData) {
        TooltipSystem.Hide();
    }

    public void OnPointerClick(PointerEventData eventData) {
        if (onClick != null && animal != null) onClick(animal);
    }

    void OnDisable() {
        TooltipSystem.Hide();
    }
}
