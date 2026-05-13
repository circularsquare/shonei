using UnityEngine;

// Smuggles a sprite's sortingOrder into the normal-capture shader via a
// per-renderer MaterialPropertyBlock. The normals RT's blue channel stores
// this bucket so LightCircle.shader can ramp effective light height based on
// the sort delta between a light and each receiver pixel — a receiver that
// sorts in front of a light gets lit as if the light were behind it.
//
// Usage:
//   - Call LightReceiverUtil.SetSortBucket(sr) anywhere code sets sr.sortingOrder
//     on a lit sprite.
//   - For sprites whose sortingOrder is baked into a prefab, attach the
//     LightReceiver component in the editor — it writes the MPB on Start.
//
// Because sortingOrder in Shonei is static per type, one write at spawn is
// sufficient; there is no per-frame cost. If a sprite's sortingOrder ever
// changes at runtime, call Refresh() (or SetSortBucket again) explicitly.
public static class LightReceiverUtil {
    static readonly int SortBucketId   = Shader.PropertyToID("_SortBucket");
    static readonly int PlantBaseYId   = Shader.PropertyToID("_PlantBaseY");
    static readonly int PlantHeightId  = Shader.PropertyToID("_PlantHeight");
    static readonly int PlantPhaseId   = Shader.PropertyToID("_PlantPhase");
    static readonly int PlantSwayId    = Shader.PropertyToID("_PlantSway");
    static readonly int UseMaskId      = Shader.PropertyToID("_UseMask");
    static readonly int SwayAmountId   = Shader.PropertyToID("_SwayAmount");
    static readonly int RoleIsHeadId   = Shader.PropertyToID("_RoleIsHead");
    static readonly int HeadCenterYId  = Shader.PropertyToID("_HeadCenterY");
    static readonly int SwayMaskTexId  = Shader.PropertyToID("_SwayMask");
    static MaterialPropertyBlock _scratch;

    // Writes _SortBucket = sortingOrder/255 onto the renderer's MPB. We read
    // any existing MPB first so we don't stomp other properties a caller (or
    // Unity's sprite pipeline) may have set.
    public static void SetSortBucket(SpriteRenderer sr) {
        if (sr == null) { Debug.LogError("LightReceiverUtil.SetSortBucket: null SpriteRenderer"); return; }
        _scratch ??= new MaterialPropertyBlock();
        sr.GetPropertyBlock(_scratch);
        _scratch.SetFloat(SortBucketId, Mathf.Clamp01(sr.sortingOrder / 255f));
        sr.SetPropertyBlock(_scratch);
    }

    // Writes the wind-sway MPB props onto a plant SR. Read-modify-write
    // pattern same as SetSortBucket — composes with whatever the caller (or
    // Unity) already set. _PlantSway is hardcoded to 1 here: the only callers
    // are plant SR setup sites, and the flag exists to gate the matching code
    // path in NormalsCapture.shader so non-plant sprites skip the sway math.
    //
    // `useMask = true` puts the SR in mask-mode (Phase 3): vertex stays put,
    // fragment shifts the texture sample by amplitude × _SwayMask R channel.
    // Used when the SR's sprite has a `_SwayMask` secondary texture authored
    // (i.e. trees with rigid trunks). `useMask = false` keeps the renderer in
    // vertex-mode (height-weighted whole-quad bend, Phase 1/2 behaviour).
    //
    // `swayAmount` linearly attenuates the computed amplitude — 1 = full sway
    // (the existing plant default), 0 = no motion (mushrooms, moss, anything
    // rigid). When 0 we also clear the _PlantSway gate so the shader skips
    // the math entirely instead of multiplying by zero.
    //
    // `useMask + roleIsHead + headCenterY + maskTexture` activate the flower
    // stem/head split (see Sway.hlsl header). `useMask = true` is only meaningful
    // when the caller is one half of a two-SR flower; `roleIsHead = true` puts
    // this SR into uniform-shift head mode (the other SR keeps stem semantics).
    // `headCenterY` is in world units relative to `baseY` and only consumed when
    // roleIsHead = true. `maskTexture` is bound as `_SwayMask` so we don't rely
    // on the SpriteSheet-secondary-texture mechanism for runtime-generated
    // masks; pass null to keep whatever is already on the SR.
    //
    // Called on plant ctor, on every extension claim, on RebuildExtensionTiles
    // (load), on ReleaseAllExtensionTiles (harvest), AND on every UpdateSprite
    // (the mask flag may flip with growth-stage sprite swaps).
    public static void SetPlantSwayMPB(SpriteRenderer sr, float baseY, float plantHeight, float phase, bool useMask, float swayAmount = 1f, bool roleIsHead = false, float headCenterY = 0f, Texture maskTexture = null) {
        if (sr == null) { Debug.LogError("LightReceiverUtil.SetPlantSwayMPB: null SpriteRenderer"); return; }
        _scratch ??= new MaterialPropertyBlock();
        sr.GetPropertyBlock(_scratch);
        _scratch.SetFloat(PlantBaseYId,  baseY);
        _scratch.SetFloat(PlantHeightId, Mathf.Max(1f, plantHeight));
        _scratch.SetFloat(PlantPhaseId,  phase);
        // Gate off when amount is exactly 0 — saves the vertex-shader work
        // for rigid decorations and avoids any sub-pixel drift from the
        // multiply-by-zero path.
        _scratch.SetFloat(PlantSwayId,   swayAmount > 0f ? 1f : 0f);
        _scratch.SetFloat(UseMaskId,     useMask ? 1f : 0f);
        _scratch.SetFloat(SwayAmountId,  Mathf.Clamp01(swayAmount));
        _scratch.SetFloat(RoleIsHeadId,  roleIsHead ? 1f : 0f);
        _scratch.SetFloat(HeadCenterYId, headCenterY);
        if (maskTexture != null) _scratch.SetTexture(SwayMaskTexId, maskTexture);
        sr.SetPropertyBlock(_scratch);
    }
}

// Project-wide factory for lit SpriteRenderers. Routes every runtime SR
// creation through `Custom/Sprite` (Resources/Materials/Sprite.mat) — a
// dual-pass shader (Universal2D + UniversalForward) that renders correctly
// under both URP renderer types AND participates in our LightFeature's
// NormalsCapture filter.
//
// Why this exists: Unity's per-renderer "default sprite material" depends on
// the active URP renderer. Under the 2D Renderer it's Sprite-Lit-Default
// (Universal2D-only); under the Universal renderer it's Sprite-Default
// (no Universal2D pass — invisible to NormalsCapture, so the sprite renders
// at constant deep-ambient color forever). Routing through this helper makes
// the project independent of that default and resilient to renderer swaps.
//
// For intentionally-unlit overlays (blueprint frames, plant harvest overlays,
// tile highlights), keep the explicit Sprite-Unlit-Default assignment instead
// of using this helper — they should NOT participate in NormalsCapture.
public static class SpriteMaterialUtil {
    static Material _cachedLit;
    static bool _probedLit;
    static Material _cachedPlant;
    static bool _probedPlant;

    // Reload-Domain-off support: cached Material refs survive across play sessions
    // but the underlying Unity objects don't. Without this, _probedLit stays true,
    // _cachedLit is a dead ref, and every sprite renderer that calls the getter
    // gets back a destroyed Material.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() {
        _cachedLit = null; _probedLit = false;
        _cachedPlant = null; _probedPlant = false;
    }

    // Loaded once from Resources/Materials/Sprite.mat. Null = asset missing;
    // callers fall through to whatever Unity's default is (with a warning).
    public static Material LitSpriteMaterial {
        get {
            if (_probedLit) return _cachedLit;
            _cachedLit = Resources.Load<Material>("Materials/Sprite");
            _probedLit = true;
            if (_cachedLit == null)
                Debug.LogError("SpriteMaterialUtil: Resources/Materials/Sprite.mat not found. " +
                               "Sprites created via AddSpriteRenderer will use Unity's default and may render incorrectly under URP Universal renderer.");
            return _cachedLit;
        }
    }

    // Plant-only variant — same lit pipeline as LitSpriteMaterial, plus
    // wind-sway vertex displacement. See PlantSprite.shader. Falls through
    // to LitSpriteMaterial on miss so plants still render (just without
    // sway) if the asset goes missing.
    public static Material PlantSpriteMaterial {
        get {
            if (_probedPlant) return _cachedPlant;
            _cachedPlant = Resources.Load<Material>("Materials/PlantSprite");
            _probedPlant = true;
            if (_cachedPlant == null)
                Debug.LogError("SpriteMaterialUtil: Resources/Materials/PlantSprite.mat not found. " +
                               "Plants will render without wind sway.");
            return _cachedPlant;
        }
    }

    // Adds a SpriteRenderer to `go` and assigns the lit-sprite material. Use
    // this anywhere a SR was previously created via plain AddComponent and was
    // relying on Unity's default material to be lit-compatible.
    public static SpriteRenderer AddSpriteRenderer(GameObject go) {
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        var mat = LitSpriteMaterial;
        if (mat != null) sr.sharedMaterial = mat;
        return sr;
    }

    // Same as AddSpriteRenderer but assigns the plant sway material. Used by
    // Plant.cs for extension SRs (anchor SR is created upstream by
    // StructureVisualBuilder via AddSpriteRenderer, and Plant overrides its
    // sharedMaterial to the plant variant after the fact — see Plant.cs).
    // Falls through to the lit material if the plant asset is missing so
    // plants still render correctly (just without sway).
    public static SpriteRenderer AddPlantSpriteRenderer(GameObject go) {
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        var mat = PlantSpriteMaterial ?? LitSpriteMaterial;
        if (mat != null) sr.sharedMaterial = mat;
        return sr;
    }
}

// MonoBehaviour variant for prefabs whose sortingOrder is authored in the
// editor rather than set by code. Writes sort-bucket MPBs on Start and on
// demand via Refresh().
//
// Default behaviour: walks all SpriteRenderers in this GameObject and its
// children (so one component on an animal root handles body, feet, arm,
// clothing — each picks up its own sortingOrder). Set `target` to restrict
// to a single renderer.
[DisallowMultipleComponent]
public class LightReceiver : MonoBehaviour {
    [Tooltip("Optional single target. Leave null to apply to every SpriteRenderer in this GameObject and its children.")]
    public SpriteRenderer target;

    void Start() {
        Refresh();
    }

    // Call this if any contained SpriteRenderer's sortingOrder changes at runtime.
    public void Refresh() {
        if (target != null) {
            LightReceiverUtil.SetSortBucket(target);
            return;
        }
        var srs = GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
        if (srs.Length == 0) {
            Debug.LogError($"LightReceiver on {name}: no SpriteRenderers found to set sort buckets for.");
            return;
        }
        for (int i = 0; i < srs.Length; i++) LightReceiverUtil.SetSortBucket(srs[i]);
    }
}
