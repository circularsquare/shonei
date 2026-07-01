using UnityEngine;

// Shows a yellow "?" prompt at the front of a storage building that is empty AND accepts
// nothing — the freshly-placed-but-never-configured state (storage starts all-disallowed;
// the player opts items in via the StoragePanel filter). Such a store silently receives no
// hauls, so the icon nudges the player to open it and pick what goes in. It vanishes the
// moment an item is allowed or something lands in the store.
//
// Rendering mirrors the plant harvest overlay (Plant.CreateHarvestOverlay): a child on the
// Unlit layer with the overlay-ambient material, so it draws after the lighting composite
// (always readable) but still dims toward night ambient rather than glaring in the dark.
//
// Attached from the Building constructor's isStorage block — not AttachAnimations, which runs
// during base() before `storage` exists (see Structure.AttachAnimations / Building ctor).
public class StorageEmptyVisuals : MonoBehaviour {
    Inventory storage;
    SpriteRenderer sr;

    // Cached sprite + layer — avoid a per-building Resources.Load and LayerMask.NameToLayer.
    // Sentinel bools so a "missing" log fires once even though the cached value stays null.
    static Sprite _sprite;
    static bool   _spriteLoaded;
    static Sprite GetSprite() {
        if (_spriteLoaded) return _sprite;
        _sprite = Resources.Load<Sprite>("Sprites/Misc/questionmark");
        if (_sprite == null)
            Debug.LogError("StorageEmptyVisuals: missing Resources/Sprites/Misc/questionmark — unconfigured-storage prompt will be invisible");
        _spriteLoaded = true;
        return _sprite;
    }
    static int  _unlitLayer = -1;
    static bool _unlitLayerLookedUp;
    static int GetUnlitLayer() {
        if (_unlitLayerLookedUp) return _unlitLayer;
        _unlitLayer = LayerMask.NameToLayer("Unlit");
        if (_unlitLayer < 0) Debug.LogError("StorageEmptyVisuals: 'Unlit' layer not found — prompt will be lit");
        _unlitLayerLookedUp = true;
        return _unlitLayer;
    }

    // parentSortingOrder = the building body's sortingOrder; the icon draws one step above it
    // (though the Unlit layer already composites it over the lit scene regardless).
    public void Init(Inventory storage, int nx, int parentSortingOrder) {
        this.storage = storage;
        if (storage == null) { enabled = false; return; }

        GameObject iconGo = new GameObject("empty_storage_prompt");
        iconGo.transform.SetParent(transform, false);
        // Front-and-centre on the footprint: horizontally centred over the anchor's tile row
        // (nx wide), at the bottom row. `go` sits at the anchor (bottom-left) tile centre, so
        // x offset centres it and y stays at the base — a low, in-front prompt, not a floater.
        iconGo.transform.localPosition = new Vector3((Mathf.Max(1, nx) - 1) / 2f, 0f, 0f);

        int unlitLayer = GetUnlitLayer();
        if (unlitLayer >= 0) iconGo.layer = unlitLayer;

        sr = iconGo.AddComponent<SpriteRenderer>();
        Material unlitMat = SpriteMaterialUtil.OverlayAmbientMaterial;
        if (unlitMat != null) sr.sharedMaterial = unlitMat;
        sr.sprite = GetSprite();
        sr.sortingOrder = parentSortingOrder + 1;
        sr.enabled = false; // Update sets true on the first frame if unconfigured
    }

    void Update() {
        if (sr == null || storage == null) return;
        // Idempotent visibility toggle. AcceptsNothing early-outs on the first allowed item,
        // so the common (configured) case is cheap; the full scan only runs while the icon shows.
        bool show = storage.IsEmpty() && storage.AcceptsNothing();
        if (sr.enabled != show) sr.enabled = show;
    }
}
