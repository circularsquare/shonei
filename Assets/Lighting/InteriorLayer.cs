using UnityEngine;

// Resolves the "Interior" Unity layer and moves a GameObject's sprite renderers onto it.
//
// Sprites on the Interior layer are captured into the lighting pipeline's directional-only
// tier (sun + ambient, no point/torch lights) — see LightFeature.directionalOnlyLayers in
// the URP renderer asset. Used for enclosed buildings (burrows) and mice standing inside
// them, so torchlight from above doesn't bleed into a buried interior. The Interior layer is
// in the world camera's culling mask, so these sprites still render normally; only their
// lighting tier changes. See SPEC-rendering §Lighting.
//
// In wall-shadows mode (SettingsManager.interiorLit) the capture pass promotes this tier from
// 0.3 to 0.5 (lit-only) via the _InteriorLit global, so interiors instead RECEIVE point lights
// and occlusion is handled per-pixel by LightCircle's wall ray-march (a torch inside lights the
// burrow; one above is blocked by the roof). No-shadows mode = the legacy "skip torches" tier.
// BurrowAsBuilding mode instead routes enclosed sprites to Default entirely (LayerForEnclosed),
// so they never reach this Interior tier — they shade as ordinary buildings.
public static class InteriorLayer {
    // Resolved on each access (not cached) — a static readonly would lock in -1 forever if
    // the layer didn't exist at type-init, masking a missing-layer setup. NameToLayer is a
    // cheap name lookup and these are only hit on tile-boundary crossings / building spawn.
    public static int Interior => LayerMask.NameToLayer("Interior");
    public static int Default  => LayerMask.NameToLayer("Default");

    // The layer an enclosed building's sprites (facade, backdrop, inside-mice) should use.
    // Normally Interior (own lighting tier). In the BurrowAsBuilding comparison mode the burrow
    // shades exactly like a surface building, so its sprites live on Default instead. Single
    // source of truth for the three layer-assignment sites (Structure ctor, the preserved-tile
    // backdrop, and Animal.RefreshInteriorRendering) + the live re-apply on settings change.
    public static int LayerForEnclosed() {
        bool asBuilding = SettingsManager.instance != null && SettingsManager.instance.burrowAsBuilding;
        return asBuilding ? Default : Interior;
    }

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
