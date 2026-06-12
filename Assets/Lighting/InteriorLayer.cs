using UnityEngine;

// Resolves the "Interior" Unity layer and moves a GameObject's sprite renderers onto it.
//
// Sprites on the Interior layer are captured into the lighting pipeline's directional-only
// tier (sun + ambient, no point/torch lights) — see LightFeature.directionalOnlyLayers in
// the URP renderer asset. Used for enclosed buildings (burrows) and mice standing inside
// them, so torchlight from above doesn't bleed into a buried interior. The Interior layer is
// in the world camera's culling mask, so these sprites still render normally; only their
// lighting tier changes. See SPEC-rendering §Lighting.
public static class InteriorLayer {
    // Resolved on each access (not cached) — a static readonly would lock in -1 forever if
    // the layer didn't exist at type-init, masking a missing-layer setup. NameToLayer is a
    // cheap name lookup and these are only hit on tile-boundary crossings / building spawn.
    public static int Interior => LayerMask.NameToLayer("Interior");
    public static int Default  => LayerMask.NameToLayer("Default");

    // Sets the Unity layer on every SpriteRenderer under root. The lighting capture filters
    // by each renderer's gameObject.layer, so this is what flips a multi-part object
    // (paper-doll mouse, multi-SR building) between the lit and directional-only tiers.
    public static void SetSpriteLayers(GameObject root, int layer) {
        if (root == null) return;
        if (layer < 0) {
            Debug.LogError("InteriorLayer.SetSpriteLayers: invalid layer — is the 'Interior' layer defined in Tags & Layers?");
            return;
        }
        var srs = root.GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
        for (int i = 0; i < srs.Length; i++) srs[i].gameObject.layer = layer;
    }
}
