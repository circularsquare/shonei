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

    Image image;
    Animal animal;

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

        gameObject.SetActive(true);
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
