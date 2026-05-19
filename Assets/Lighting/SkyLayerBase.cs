using UnityEngine;

// Common base for every Sky-layer script that builds runtime sprite
// state (procedural RenderTextures, dynamic Texture2Ds, child
// SpriteRenderers) and must survive Play-mode editor events that
// would otherwise leave the visible sprite stuck as a flat tinted
// rectangle. Subclasses today: CloudLayer, BackgroundLayer, HazeLayer,
// SkyGradient, StarField.
//
// ── Play-mode resilience ───────────────────────────────────────────
// Two distinct editor events that this base class shields subclasses
// from:
//
// 1) **Domain reload** (script recompile during Play). Non-
//    `[SerializeField]` private fields reset to null while the
//    serialized scene state (child GameObjects, SpriteRenderers)
//    survives. Without recovery, the SR keeps rendering its dummy
//    backing texture tinted by `sr.color` — a flat coloured rectangle
//    the exact shape of the sprite quad.
//
// 2) **Asset reimport** (e.g. user edits a sprite asset the running
//    game already has loaded). Unity refreshes the asset DB without
//    nulling any runtime C# refs, but it silently clears the
//    SpriteRenderer's `MaterialPropertyBlock` — so any MPB-bound
//    `_MainTex` override (the procedural RT) drops back to the
//    sprite's source texture. Same visual symptom, different cause.
//    Subclasses that use MPB-bound textures MUST re-apply the MPB
//    binding inside DoLateUpdate every frame — this base doesn't
//    know about per-subclass MPB shape, but the `DoLateUpdate` hook
//    is the right place to put it.
//
// ── Lifecycle contract ─────────────────────────────────────────────
// • Start → EnsureInitialized → resolve camera, optionally pin Sky
//   layer, destroy any leftover children, BuildContents().
// • LateUpdate → if state was lost (post-reload), re-run
//   EnsureInitialized; then DoLateUpdate.
// • OnDestroy stays a subclass concern (RT releases, runtime material
//   destroys, etc. — varies by subclass).
//
// ── Footgun ────────────────────────────────────────────────────────
// Do NOT declare `void Start()` or `void LateUpdate()` in a subclass.
// Unity's message dispatch would call both the base and the subclass
// methods (or the subclass would shadow), bypassing the resilience
// guard. Override BuildContents and DoLateUpdate instead.
public abstract class SkyLayerBase : MonoBehaviour {
    protected Camera bgCam;
    bool initialized;

    // Override to provide a different resolution strategy. Default
    // chain: parent's Camera component → SkyCamera.instance.BgCam →
    // Camera.main. Covers both child-of-SkyCamera layouts and
    // free-floating layouts that find SkyCamera via the singleton.
    protected virtual Camera ResolveCamera() {
        if (transform.parent != null) {
            var c = transform.parent.GetComponent<Camera>();
            if (c != null) return c;
        }
        if (SkyCamera.instance != null && SkyCamera.instance.BgCam != null) {
            return SkyCamera.instance.BgCam;
        }
        return Camera.main;
    }

    // Override → true on layers that should pin their own GameObject
    // onto Unity's "Sky" layer at every init (defends against an
    // accidental reparent putting the GO on a different layer, which
    // would knock the sprite out of SkyCamera's culling mask).
    // Subclasses that live as children of SkyCamera typically inherit
    // the parent's layer and leave this false.
    protected virtual bool ManageSkyLayer => false;

    // Build all runtime sprite state. Base has already resolved bgCam
    // and destroyed any leftover children before calling this.
    protected abstract void BuildContents();

    // Per-frame work. Called only after init is healthy. MPB
    // re-binding (if the subclass uses MPB-bound textures) belongs at
    // the top of this method — see the asset-reimport note above.
    protected abstract void DoLateUpdate();

    void Start() {
        EnsureInitialized();
    }

    protected void EnsureInitialized() {
        initialized = false;

        bgCam = ResolveCamera();
        if (bgCam == null) {
            Debug.LogError($"{GetType().Name}: failed to resolve a camera. Disabling.");
            enabled = false;
            return;
        }

        if (ManageSkyLayer) {
            int skyLayer = LayerMask.NameToLayer("Sky");
            if (skyLayer >= 0) gameObject.layer = skyLayer;
        }

        // Nuke any leftover children (sprite child GameObjects from a
        // previous Play session that survived domain reload but whose
        // backing C# state we just lost). BuildContents rebuilds them
        // fresh.
        for (int i = transform.childCount - 1; i >= 0; i--) {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }

        BuildContents();
        initialized = true;
    }

    void LateUpdate() {
        if (!initialized || bgCam == null) {
            EnsureInitialized();
            if (!initialized) return;
        }
        DoLateUpdate();
    }
}
