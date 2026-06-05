using Unity.Profiling;
using UnityEngine;

// Procedural cloud field. _MainTex and _NormalMap are RTs generated each
// frame on the GPU via Graphics.Blit through "Hidden/CloudFieldGen" and
// bound on the SpriteRenderer via MaterialPropertyBlock.
//
// Design — sphere-blob shading, anchored noise grid, metaball alpha,
// finite-difference normal, flat normalRT for NormalsCapture, perf
// budget, future shading headroom — lives in SPEC-rendering.md §Cloud
// system. Keep this header for code-adjacent notes only.
//
// LateUpdate runs at order 100, after SkyCamera's default-order
// LateUpdate has moved the camera to follow Main; at that point
// bgCam.transform.position equals the intended sprite origin, so we
// place, generate blobs, push uniforms, and Blit before URP renders.
//
// EnsureInitialized is idempotent — LateUpdate re-runs it after a
// Play-mode recompile / domain reload (same pattern as SkyGradient).
//
// Hash2D / ValueNoise here MUST stay in sync with Noise.hlsl — CPU
// blob placement and shader noise sampling depend on identical output.
[DefaultExecutionOrder(100)]
public class CloudLayer : SkyLayerBase {
    [Header("Field texture")]
    [Tooltip("Pixels-per-world-unit. Half the project's main PPU (8 vs 16) gives 2x2 screen pixels per texel — still reads as pixel art.")]
    public float pixelsPerUnit = 8f;
    // Texture dimensions are now derived from the camera viewport (width)
    // and the band envelope (height), not inspector-set. Updated by
    // RebuildRTsAndSprite. Kept as a field so other code (shader uniform
    // push) can read the current dims.
    [System.NonSerialized] public Vector2Int textureSize;

    [Tooltip("World-units → noise-units multiplier. Smaller = larger smooth cloud features; larger = noisier, more broken-up shapes.")]
    public float noiseScale = 0.15f;
    [Tooltip("Horizontal stretch of the noise field. 1 = isotropic. >1 = features wider than tall (canonical horizontal-cumulus shapes). 2 default gives clouds about twice as wide as they are tall.")]
    public float noiseAspect = 2.0f;
    [Tooltip("Max per-cell density HEAD START — INDEPENDENT per grid cell (unlike blobJitter, which only nudges a blob's position within its cell). A cell that rolls high crosses the spawn threshold early and grows into a dominant lobe while neighbours lag and appear to grow out of it — breaking up the 'same-size dotted line'. One-sided (cells only ever advance), shaped by densityJitterSkew. ~0.05 = subtle; higher = more ragged edges.")]
    [Range(0f, 0.2f)] public float densityJitter = 0.05f;
    [Tooltip("Skew of the per-cell head-start roll (exponential curve). 1 ≈ uniform (head starts evenly spread → flat-looking edge). Higher = right-skewed: MOST cells get ~no head start (cross together) and only a FEW roll high and pop in early as dominant lobes. ~4 = a few clear dominants.")]
    [Range(1f, 10f)] public float densityJitterSkew = 4f;
    [Tooltip("How fast each cell's head-start roll drifts over time (multiplies the cloud evolution clock). 0 = static dominant blobs; higher = edges restructure faster. Kept slow so jitter-driven threshold crossings stay gradual (no popping).")]
    [Range(0f, 40f)] public float densityJitterRate = 8f;

    [Header("Coverage by humidity")]
    [Tooltip("Density threshold at humidity=0 (clear). Higher = less coverage (fewer blobs spawn).")]
    [Range(0f, 1f)] public float thresholdClear = 0.65f;
    [Tooltip("Density threshold at humidity=1 (overcast). Lower = more coverage (more blobs spawn).")]
    [Range(0f, 1f)] public float thresholdStorm = 0.40f;

    [Header("Sky band (absolute world y)")]
    [Tooltip("World-y centre of the cloud field. The sprite is positioned at this y regardless of camera y, so clouds only appear when the camera viewport reaches this altitude.")]
    public float bandCenterY = 14f;
    [Tooltip("Half-height of the band, in world units. The envelope falls to 0 at +bandHalfHeight above the sprite centre.")]
    public float bandHalfHeight = 2.5f;
    [Tooltip("How tall the band's bottom half is relative to the top. 1 = symmetric (round-bottomed clouds). <1 = compressed bottom (flat-bottomed cumulus-like clouds). 0.4 default gives a recognizable flat base.")]
    [Range(0.1f, 1f)] public float bandBottomScale = 0.4f;
    [Tooltip("World-z of the cloud sprite. Must sit between SkyCamera's near and far clip planes.")]
    public float renderZ = 5f;

    [Header("Wind & parallax")]
    [Tooltip("World units/sec per unit of wind. Drift accumulates into the noise sample offset.")]
    public float windDriftScale = 0.6f;
    [Tooltip("Horizontal parallax. 0 = sky-locked (clouds glued to viewport, no apparent motion); 1 = world-locked (full parallax, clouds move 100% as much as foreground). 0.25 = clouds appear to move 25% as much as foreground (canonical 'distant sky' look).")]
    [Range(0f, 1f)] public float worldLockingX = 0.25f;
    [Tooltip("Vertical parallax. 0 = sky-locked (sprite tracks camera y exactly, clouds glued to viewport vertically); 1 = world-locked (sprite stays at bandCenterY, clouds appear to move 100% as much as foreground vertically). 0.25 = 25% parallax. Under SkyCamera zoom-dampening the apparent ratio drifts slightly off this value when the main camera is zoomed out — tune to taste.")]
    [Range(0f, 1f)] public float worldLockingY = 0.25f;
    [Tooltip("Rate at which the underlying noise field morphs over time (noise-units / second on the time axis of a 3D value noise). 0 = static field (only wind moves it); ~0.05 = clouds visibly form / dissolve over tens of seconds, like real cumulus evolving in place. Independent of wind drift, which just slides the field horizontally.")]
    public float cloudEvolutionRate = 0.05f;

    [Header("Tint")]
    public Color baseColorClear = Color.white;
    public Color baseColorStorm = new Color(0.40f, 0.42f, 0.48f, 1f);
    [Tooltip("Humidity below this keeps the cloud tint pinned exactly at baseColorClear; above, it lerps toward baseColorStorm as humidity goes from this value up to 1. ~0.4 = clouds stay crisp white through dry weather and only start greying as humidity approaches rain (WeatherSystem.rainThreshold = 0.7), instead of dirtying up linearly from humidity=0. Set to 0 for the old straight-from-clear-to-storm behaviour.")]
    [Range(0f, 1f)] public float tintLerpStartHumidity = 0.4f;

    [Header("Cloud shading — 3 colour bands")]
    [Tooltip("Colour used for sunlit pixels (the brightest band).")]
    public Color litColor    = new Color(1.00f, 0.98f, 0.92f, 1f);
    [Tooltip("Colour used for mid-tone pixels (the default cloud body).")]
    public Color midColor    = new Color(0.78f, 0.80f, 0.85f, 1f);
    [Tooltip("Colour used for shadowed pixels (the darkest band).")]
    public Color shadowColor = new Color(0.45f, 0.50f, 0.60f, 1f);
    [Tooltip("Lambertian value above this → sunlit band. Lower = larger sunlit area.")]
    [Range(0f, 1f)] public float litBand    = 0.55f;
    [Tooltip("Lambertian value below this → shadow band. Higher = larger shadow area.")]
    [Range(0f, 1f)] public float shadowBand = 0.20f;
    [Tooltip("Cloud-specific sun elevation, overriding the scene's _SunHeight. Higher = the cloud's terminator visibly curves (moon-phase look on each sphere); lower = terminator straightens. Decoupled from the scene's actual sun so clouds keep their crescent-style shading regardless of the day-cycle sun angle. _SunDir.xy is still tracked for the lighting direction.")]
    public float cloudSunHeight = 1.5f;

    [Header("Sphere-blobs")]
    [Tooltip("Grid cell size in world units. Smaller = denser blob spawn, more solid clouds. Blob radii should overlap adjacent cells (radiusMax ≈ cellSize) so clouds merge visually. Floored at 0.2 — smaller values explode the per-bake grid-cell count and hang the editor.")]
    [Min(0.2f)] public float blobCellSize = 1.0f;
    [Tooltip("How much each blob jitters within its cell (0=grid-aligned, 1=full cell). Hashed per cell, deterministic frame-to-frame.")]
    [Range(0f, 1f)] public float blobJitter = 0.5f;
    [Tooltip("Minimum blob radius (world units) — used when density barely exceeds threshold.")]
    public float blobRadiusMin   = 0.7f;
    [Tooltip("Maximum blob radius (world units) — used when density is well above threshold. Should overlap neighbouring cells so cloud bodies look continuous.")]
    public float blobRadiusMax   = 1.4f;
    [Tooltip("Excess density (above spawn threshold) at which a cell's target radius saturates at blobRadiusMax. Lower = more cells reach max-size territory, denser-feeling clouds; higher = only the strongest noise peaks hit max size, so most cells lean toward blobRadiusMin. With the per-cell random size factor, this controls how big the BIGGEST possible blob in a cluster is — lower values broaden the spread upward. Default 0.3 means cells need noise ≈ threshold + 0.3 to spawn a full-size blob; with typical thresholds near 0.5 and noise capped at 1.0, that's a fairly rare peak.")]
    public float excessForMaxSize = 0.3f;
    [Tooltip("Range of z-jitter per blob (world units). Spreads blobs in depth so the front-of-cloud isn't a flat plane; visible as varied silhouette overlap.")]
    public float blobDepthRange  = 0.5f;
    [Tooltip("Finite-difference step (world units) for the height-field surface-normal gradient in the shader. Smaller = sharper per-blob facets and more visible seam ridges between overlapping blobs; larger = smoother blending across seams but blurs fine surface detail (e.g., small blobs lose their 3D feel). 0.15 is a balanced default; try 0.05 for crisper bumps or 0.3 for very smooth cumulus.")]
    [Range(0.02f, 0.5f)] public float normalEpsilon = 0.15f;
    [Tooltip("Strength of per-pixel silhouette wobble — domain-warps the metaball alpha sample by up to this many world units. Silhouette ripples by ~strength; interior shading is unaffected (un-warped position drives normals). ~0.2 = subtle bumps; ~0.5 = clearly wavy cumulus; >1.0 = wispy / stringy edges.")]
    [Range(0f, 1.5f)] public float edgeWobbleStrength = 0.4f;
    [Tooltip("Frequency of the wobble noise field. Higher = finer-grained bumps along the silhouette; lower = broader undulations. ~1 gives bump features at roughly one-world-unit scale; ~2-3 gives the kind of fine fingers/wisps you'd see on a wispy cumulus.")]
    public float edgeWobbleScale = 1.5f;
    [Tooltip("Finite-difference step inside the curl-noise that drives the silhouette warp. Smaller = sharper / more pinched warp features (the curl gradient picks up high-frequency detail); larger = smoother, broader warp swirls. Composes with edgeWobbleScale — together they shape the size and texture of the bump pattern. 0.05 = default; try 0.02 for spiky / wispy, 0.15 for languid / billowy.")]
    [Range(0.01f, 0.3f)] public float curlNoiseEps = 0.05f;
    [Tooltip("Horizontal stretch of each blob into an ellipsoid. 1 = perfect spheres; 1.5-2 = cumulus-like elongated lobes; >2.5 = obvious cigars. Composes with noiseAspect (which stretches where blobs cluster) — together they shape both the macro and the micro of horizontal cloud appearance. Per-blob aspect varies ±20% around this value (hashed per cell, stable frame-to-frame) so adjacent blobs don't all look like identical stretched ovals.")]
    [Range(1f, 3f)] public float blobAspect = 1.5f;
    [Tooltip("Edge threshold (centre of the metaball alpha smoothstep). Lower = more generous silhouette (blobs read solid further from their centres); higher = tighter cloud bodies. Single-blob silhouettes extend roughly to dist = sqrt(1 - threshold) * radius before the alpha begins fading.")]
    [Range(0.0f, 0.6f)]  public float edgeThreshold = 0.2f;
    [Tooltip("Edge softness (half-width of the metaball alpha smoothstep). 0 = razor-hard silhouette; ~0.1 = subtle feather; >0.3 = visibly wispy. Combines with strength of warp noise — softer edges + bigger warp = puffy cumulus, sharper edges = more cartoony.")]
    [Range(0.0f, 0.4f)]  public float edgeSoftness  = 0.15f;
    [Tooltip("Density window (excess above the spawn threshold) over which a blob ramps from radius 0 to full. Wider = blobs grow/shrink more gradually as the noise field drifts a cell across the threshold; narrower = crisper but blobs snap in. ~0.08 = a gentle fade.")]
    [Range(0.005f, 0.2f)] public float fadeRange = 0.08f;
    [Tooltip("Exponent of the radius fade-in curve (radius grows as excess^exponent over fadeRange). <1 front-loads growth so fringe cells reach a mergeable size quickly and spend little time tiny — fewer scattered specks at the cloud boundary. 0.5 = sqrt (strong front-load), 1 = linear (more time small), ~0.8 = mild front-load.")]
    [Range(0.2f, 1.5f)] public float fadeExponent = 0.8f;
    [Tooltip("Minimum blob DIAMETER in pixels; smaller blobs are culled. These are the shrinking fringe blobs near the spawn threshold. ~1 = invisible vanish (no pop). Higher (e.g. 2-3) trims more boundary specks at the cost of a small pop when they cull. Resolution-aware via pixelsPerUnit.")]
    [Range(0.5f, 5f)] public float minBlobPixels = 2f;

    SpriteRenderer sr;
    // Dummy Texture2D backing for Sprite.Create — never written to. The
    // SpriteRenderer's _MainTex is MPB-overridden to mainRT, so this
    // texture's contents are irrelevant; it exists only because
    // Sprite.Create requires a Texture2D and the sprite's UV/rect need
    // valid dimensions to match the RT.
    Texture2D spriteTex;
    RenderTexture mainRT, normalRT;
    Material cloudGenMat;
    MaterialPropertyBlock mpb;
    float windOffsetX;
    // Z-axis offset into the 3D value noise field. Grows linearly at
    // cloudEvolutionRate per second so the sampled noise pattern
    // smoothly evolves in place — independent of wind drift, which
    // slides the field horizontally instead.
    float evolutionOffset;

    // Blob buffers — allocated once at MAX_BLOBS, reused every frame.
    // SetVectorArray / SetFloatArray lock the array length on first
    // call; reallocation would force a fresh upload of the new size, so
    // the size is fixed. Must match MAX_BLOBS in CloudFieldGen.shader.
    // Parallel arrays:
    //   blobBuffer       — (x, y, z, radius) per blob.
    //   blobAspectBuffer — horizontal stretch factor per blob.
    const int MAX_BLOBS = 800;
    readonly Vector4[] blobBuffer       = new Vector4[MAX_BLOBS];
    readonly float[]   blobAspectBuffer = new float[MAX_BLOBS];
    int blobCount;

    // Throttle for the MAX_BLOBS-exceeded warning so it doesn't spam the
    // console every bake when a dense sky genuinely wants more than the cap.
    float nextBlobCapWarnTime;

    static readonly int MainTexId        = Shader.PropertyToID("_MainTex");
    static readonly int NormalMapId      = Shader.PropertyToID("_NormalMap");
    static readonly int TexSizeId        = Shader.PropertyToID("_TexSize");
    static readonly int InvPpuId         = Shader.PropertyToID("_InvPpu");
    static readonly int LitColorId       = Shader.PropertyToID("_LitColor");
    static readonly int MidColorId       = Shader.PropertyToID("_MidColor");
    static readonly int ShadowColorId    = Shader.PropertyToID("_ShadowColor");
    static readonly int LitBandId        = Shader.PropertyToID("_LitBand");
    static readonly int ShadowBandId     = Shader.PropertyToID("_ShadowBand");
    static readonly int CloudSunHeightId = Shader.PropertyToID("_CloudSunHeight");
    static readonly int NoiseOffsetId    = Shader.PropertyToID("_NoiseOffset");
    static readonly int WobbleStrengthId = Shader.PropertyToID("_EdgeWobbleStrength");
    static readonly int WobbleScaleId    = Shader.PropertyToID("_EdgeWobbleScale");
    static readonly int CurlEpsId        = Shader.PropertyToID("_CurlEps");
    static readonly int NormalEpsilonId  = Shader.PropertyToID("_NormalEpsilon");
    static readonly int EdgeThresholdId  = Shader.PropertyToID("_EdgeThreshold");
    static readonly int EdgeSoftnessId   = Shader.PropertyToID("_EdgeSoftness");
    static readonly int BlobsId          = Shader.PropertyToID("_Blobs");
    static readonly int BlobAspectsId    = Shader.PropertyToID("_BlobAspects");
    static readonly int BlobCountId      = Shader.PropertyToID("_BlobCount");
    static readonly int FlatLightingId   = Shader.PropertyToID("_CloudFlatLighting");

    // CloudLayer normalises its own GO onto the Sky layer in case the
    // inspector / a reparent knocked it off (sprite would fall into a
    // culling gap between Main and SkyCamera otherwise).
    protected override bool ManageSkyLayer => true;

    protected override void BuildContents() {
        // Load gen shader. Lives under Resources/ so we can pull it in by
        // string without an Inspector reference — keeps CloudLayer
        // self-contained.
        Shader genShader = Resources.Load<Shader>("Shaders/CloudFieldGen");
        if (genShader == null) {
            Debug.LogError("CloudLayer: missing shader Resources/Shaders/CloudFieldGen.shader — disabling.");
            enabled = false;
            return;
        }
        cloudGenMat = new Material(genShader) { hideFlags = HideFlags.HideAndDontSave };

        // Build the sprite renderer GO. Sprite + RTs are wired in
        // RebuildRTsAndSprite which is also called on viewport changes.
        var srGo = new GameObject("CloudFieldSprite");
        srGo.transform.SetParent(transform, worldPositionStays: false);
        srGo.layer = gameObject.layer;
        sr = SpriteMaterialUtil.AddSpriteRenderer(srGo);
        sr.sortingLayerName = "Background";
        sr.sortingOrder = 0;

        mpb = new MaterialPropertyBlock();

        RebuildRTsAndSprite(ComputeNeededWidth(), ComputeNeededHeight());

        // Plant the sprite at its intended world position immediately so
        // the first-frame render is correct before DoLateUpdate runs.
        Vector3 startCam = bgCam.transform.position;
        float startSpriteY = startCam.y + (bandCenterY - startCam.y) * worldLockingY;
        sr.transform.position = new Vector3(startCam.x, startSpriteY, renderZ);

        // Anchor parallax-compensation snapshots to current camera so the
        // first per-frame sprite-position calc collapses to sky-lock.
        bakedCamX = startCam.x;
        bakedWindOffsetX = windOffsetX;
    }

    // Shifts the cloud field by a large random offset so the noise pattern
    // looks distinct from any other run. Called on new-world creation
    // (WorldController.GenerateDefault). The magnitude is >> any plausible
    // cellSize, so the resulting sample is uncorrelated with the previous
    // pattern. Baked snapshot is kept in sync so the sprite doesn't visibly
    // slide on the first frame after the shift.
    public void RandomizePattern(float offset) {
        windOffsetX = offset;
        bakedWindOffsetX = offset;
    }

    // Width is driven by the camera viewport so off-screen pixels aren't
    // generated. Pad by blobRadiusMax so partial-blob bodies at viewport
    // edges still spawn without popping at the seam.
    int ComputeNeededWidth() {
        float viewW = bgCam.orthographicSize * bgCam.aspect * 2f;
        return Mathf.CeilToInt((viewW + 2f * blobRadiusMax) * pixelsPerUnit);
    }

    // Height covers the cloud band centred on bandCenterY. Sprite is
    // symmetric in y (centred at bandCenterY), and must reach the band's
    // furthest extreme = +bandHalfHeight (the bottom only goes
    // bandHalfHeight*bandBottomScale down, never further). Pad for blob
    // radii poking past the band edge.
    int ComputeNeededHeight() {
        return Mathf.CeilToInt((2f * bandHalfHeight + 2f * blobRadiusMax) * pixelsPerUnit);
    }

    // (Re)create the procedural RTs + dummy-backed sprite at the requested
    // pixel dimensions. Called once from BuildContents and again whenever
    // the camera viewport changes meaningfully (zoom / window resize).
    // Cheap on first call; on resize, releases the old RTs/sprite and
    // re-binds the MPB to the new RTs in one shot so there's no glitch
    // frame where _MainTex falls back to the stale dummy texture.
    void RebuildRTsAndSprite(int w, int h) {
        if (mainRT != null)    { mainRT.Release();   mainRT = null; }
        if (normalRT != null)  { normalRT.Release(); normalRT = null; }
        if (spriteTex != null) { DestroyImmediate(spriteTex); spriteTex = null; }
        var oldSprite = sr.sprite;

        // Linear (sRGB=false) is critical for normalRT — sRGB would warp
        // the (rgb*2 - 1) decode in NormalsCapture.shader. Same flags on
        // mainRT for symmetry.
        mainRT   = MakeRT(w, h);
        normalRT = MakeRT(w, h);

        // Dummy backing for Sprite.Create — never read at runtime; the
        // SpriteRenderer's _MainTex is MPB-overridden to mainRT.
        spriteTex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point,
            hideFlags  = HideFlags.HideAndDontSave,
        };

        // FullRect mesh, NOT the default Tight. Tight builds the sprite
        // mesh from texture alpha at Create time — and the dummy backing
        // is all-zeros, so a Tight mesh would have no triangles and the
        // sprite would render nothing forever after.
        var newSprite = Sprite.Create(spriteTex, new Rect(0, 0, w, h),
                                      new Vector2(0.5f, 0.5f), pixelsPerUnit,
                                      extrude: 0, meshType: SpriteMeshType.FullRect);
        sr.sprite = newSprite;
        if (oldSprite != null) DestroyImmediate(oldSprite);

        // Bind both procedural RTs via MPB. Override _MainTex (normally
        // auto-bound to the sprite's source texture) so the SpriteRenderer
        // samples the procedural mainRT instead of the empty dummy.
        // MaterialPropertyBlock isn't Unity-serializable, so a Play-mode
        // recompile can wipe the field — recreate defensively.
        if (mpb == null) mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(MainTexId,   mainRT);
        mpb.SetTexture(NormalMapId, normalRT);
        sr.SetPropertyBlock(mpb);

        textureSize = new Vector2Int(w, h);
    }

    // Recreate RTs + sprite if the viewport-derived dimensions have
    // shifted significantly. 10% slack so smooth zoom doesn't thrash
    // RT allocation every frame.
    void MaybeResizeForViewport() {
        int neededW = ComputeNeededWidth();
        int neededH = ComputeNeededHeight();
        bool widthShift  = Mathf.Abs(neededW - textureSize.x) > textureSize.x * 0.1f;
        bool heightShift = Mathf.Abs(neededH - textureSize.y) > textureSize.y * 0.1f;
        if (!widthShift && !heightShift) return;
        RebuildRTsAndSprite(neededW, neededH);
    }

    RenderTexture MakeRT(int w, int h) {
        var rt = new RenderTexture(w, h, depth: 0, RenderTextureFormat.ARGB32,
                                   RenderTextureReadWrite.Linear) {
            filterMode       = FilterMode.Point,
            wrapMode         = TextureWrapMode.Clamp,
            useMipMap        = false,
            autoGenerateMips = false,
            useDynamicScale  = false,
            hideFlags        = HideFlags.HideAndDontSave,
        };
        rt.Create();
        return rt;
    }

    void OnDestroy() {
        if (mainRT != null)      { mainRT.Release();   mainRT = null; }
        if (normalRT != null)    { normalRT.Release(); normalRT = null; }
        if (spriteTex != null)   Destroy(spriteTex);
        if (cloudGenMat != null) Destroy(cloudGenMat);
    }

    // CPU-side marker — sampled by Components/GpuStatsHUD.cs. Sky layers
    // generate their RTs via Graphics.Blit (not CommandBuffer), so we can't
    // bracket GPU work here without a larger refactor; CPU dispatch time is
    // still useful as an order-of-magnitude signal.
    public const string MarkerName = "Shonei.CloudLayer";
    static readonly ProfilerMarker s_marker = new(MarkerName);

    // Throttle the expensive blob-gen + 2× Blit to ~15Hz. Content barely
    // changes per frame at 60Hz (cloud evolution = 0.05 noise-units/sec,
    // wind drift typically <1 world-unit/sec), so regenerating every
    // frame is wasted. Cheap per-frame work (sprite pos, tint, drift
    // accumulators, shader globals) still runs every frame so motion
    // and hill-shadow sync stay smooth.
    const float heavyUpdateInterval = 1f / 15f;
    float nextHeavyUpdateTime;

    // Snapshot of camera x + wind offset at the LAST heavy update. Used
    // to compensate the sprite position between bakes so the BAKED blob
    // content (which reflects the world state at bake time) appears at
    // the correct parallax/wind drift rate every frame, not just at
    // 15Hz. Without this the clouds visibly lag behind the world during
    // pans and judder with wind. Initialized in BuildContents.
    float bakedCamX;
    float bakedWindOffsetX;

    protected override void DoLateUpdate() {
        using var _ = s_marker.Auto();

        // ── Cheap per-frame work ─────────────────────────────────────────
        // Accumulate wind into a horizontal noise offset. Subtracts
        // because lp / blob sprite-local x is `anchorX − noiseOffset.x`:
        // for positive (rightward) wind to drift clouds rightward,
        // noiseOffset.x must DECREASE so anchorX − noiseOffset.x grows
        // and content slides right in the sprite.
        float wind = WeatherSystem.instance != null ? WeatherSystem.instance.wind : 0f;
        windOffsetX -= wind * windDriftScale * Time.deltaTime;
        // Cloud evolution along the noise field's third axis — slow
        // morph in place.
        evolutionOffset += cloudEvolutionRate * Time.deltaTime;

        Vector3 camPos = bgCam.transform.position;

        // Humidity-driven full-sprite tint. Multiplied by _MainTex.rgb in
        // the sprite shader. Stays pure baseColorClear below
        // tintLerpStartHumidity, then lerps to baseColorStorm.
        float humidity = WeatherSystem.instance != null
            ? WeatherSystem.instance.humidity : WeatherSystem.humidityMean;
        float tintT = Mathf.InverseLerp(tintLerpStartHumidity, 1f, humidity);
        sr.color = Color.Lerp(baseColorClear, baseColorStorm, tintT);

        float threshold = Mathf.Lerp(thresholdClear, thresholdStorm, humidity);

        // Broadcast cloud drift state as shader globals so other layers
        // (background hills' shadow overlay, etc.) can render features
        // moving in sync with clouds without coupling to CloudLayer.
        // _CloudThreshold matches the spawn threshold so a consumer
        // sampling the same noise gets roughly matching coverage.
        // Hill shadows update every frame, so these globals must too.
        Shader.SetGlobalFloat("_CloudWindOffsetX",     windOffsetX);
        Shader.SetGlobalFloat("_CloudEvolutionOffset", evolutionOffset);
        Shader.SetGlobalFloat("_CloudThreshold",       threshold);

        // ── Throttled heavy work (~15Hz) ─────────────────────────────────
        // Unscaled time: while paused (Time.timeScale=0) the user can still
        // RMB-pan the camera. If the throttle gated on Time.time, no re-bake
        // would fire and bakedCamX would freeze — the per-frame parallax-
        // compensation formula then shifts the sprite progressively off-
        // screen as the camera pans further. The bake CONTENT still freezes
        // correctly because windOffsetX / evolutionOffset use Time.deltaTime.
        if (Time.unscaledTime >= nextHeavyUpdateTime) {
            nextHeavyUpdateTime = Time.unscaledTime + heavyUpdateInterval;

            // Snapshot camera+wind for parallax compensation in the next
            // interval's cheap per-frame updates. The bake below uses these
            // exact values (camPos.x and windOffsetX) to compute noiseOffset
            // → blob.lx, so storing them here ensures the per-frame sprite
            // position formula stays consistent with the baked content.
            bakedCamX = camPos.x;
            bakedWindOffsetX = windOffsetX;

            // Resize RTs if camera viewport changed meaningfully (zoom / resize).
            MaybeResizeForViewport();

            // Defensive: domain reload (script recompile while in Play mode)
            // can leave the RT object alive but with its GPU contents dropped.
            // Cheap when already created.
            if (!mainRT.IsCreated())   mainRT.Create();
            if (!normalRT.IsCreated()) normalRT.Create();

            // Re-apply the MPB binding. Editor events (sprite reimport, material
            // refresh, OnValidate paths) can silently clear the SpriteRenderer's
            // MaterialPropertyBlock — at which point _MainTex falls back to the
            // dummy spriteTex and the cloud reads as a flat rectangle.
            if (mpb == null) mpb = new MaterialPropertyBlock();
            sr.GetPropertyBlock(mpb);
            mpb.SetTexture(MainTexId,   mainRT);
            mpb.SetTexture(NormalMapId, normalRT);
            sr.SetPropertyBlock(mpb);

            // Noise offset = parallax × camera-x + accumulated wind drift.
            // Used by GenerateBlobs to convert noise-anchored positions to
            // sprite-local coords.
            Vector2 noiseOffset = new Vector2(camPos.x * worldLockingX + windOffsetX, 0f);

            cloudGenMat.SetVector(TexSizeId,        new Vector2(textureSize.x, textureSize.y));
            cloudGenMat.SetFloat (InvPpuId,         1f / pixelsPerUnit);
            cloudGenMat.SetColor (LitColorId,       litColor);
            cloudGenMat.SetColor (MidColorId,       midColor);
            cloudGenMat.SetColor (ShadowColorId,    shadowColor);
            cloudGenMat.SetFloat (LitBandId,        litBand);
            cloudGenMat.SetFloat (ShadowBandId,     shadowBand);
            cloudGenMat.SetFloat (CloudSunHeightId, cloudSunHeight);
            cloudGenMat.SetVector(NoiseOffsetId,    noiseOffset);
            cloudGenMat.SetFloat (WobbleStrengthId, edgeWobbleStrength);
            cloudGenMat.SetFloat (WobbleScaleId,    edgeWobbleScale);
            cloudGenMat.SetFloat (CurlEpsId,        curlNoiseEps);
            cloudGenMat.SetFloat (NormalEpsilonId,  normalEpsilon);
            cloudGenMat.SetFloat (EdgeThresholdId,  edgeThreshold);
            cloudGenMat.SetFloat (EdgeSoftnessId,   edgeSoftness);
            // Flat-lighting toggle: when on, Pass 0 skips the 5-tap height-field
            // normal + Lambertian band selection (saves ~80% of blob-loop work).
            bool flatLight = SettingsManager.instance != null
                          && !SettingsManager.instance.cloudLightingEnabled;
            cloudGenMat.SetFloat(FlatLightingId, flatLight ? 1f : 0f);

            GenerateBlobs(threshold, noiseOffset);
            cloudGenMat.SetVectorArray(BlobsId,       blobBuffer);
            cloudGenMat.SetFloatArray (BlobAspectsId, blobAspectBuffer);
            cloudGenMat.SetInt        (BlobCountId,   blobCount);

            // Pass 0 → mainRT (blob-shaded 3-band colour + soft union mask).
            // Pass 1 → normalRT (flat tangent normal so the global lightmap
            // contributes uniform brightness across the cloud).
            Graphics.Blit(null, mainRT,   cloudGenMat, 0);
            Graphics.Blit(null, normalRT, cloudGenMat, 1);
        }

        // ── Sprite position (always last) ────────────────────────────────
        // Run AFTER any bake so bakedCamX/bakedWindOffsetX in the formula
        // reflect the just-baked anchors. If positioning ran before the
        // bake, the bake frame would render new RT content at the old
        // sprite anchor — visible as a 15Hz jitter while panning (most
        // noticeable when paused, where the unscaled-time throttle keeps
        // bakes firing but per-frame wind drift is zero so there's no
        // motion to mask the jump).
        //
        // Vertical: sprite tracks camera y with worldLockingY interpolating
        // between sky-locked (=0) and world-locked (=1).
        //
        // Horizontal: at bake time the sprite sits at camPos.x with blob.lx
        // values computed from noiseOffset = camPos.x * worldLockingX +
        // windOffsetX. Between bakes the BAKED lx values are fixed, so the
        // sprite must do the parallax-and-wind drift itself or the visual
        // reverts to sky-locked + windless. Formula collapses to camPos.x
        // immediately after a bake and drifts by (1 - worldLockingX) ×
        // camMotion + windDrift until the next bake.
        float spriteX = camPos.x
                      - worldLockingX * (camPos.x - bakedCamX)
                      + (bakedWindOffsetX - windOffsetX);
        float spriteY = camPos.y + (bandCenterY - camPos.y) * worldLockingY;
        sr.transform.position = new Vector3(spriteX, spriteY, renderZ);
    }

    // Walk a regular grid anchored in NOISE-SPACE (not sprite-local),
    // sample the same noise function the shader-side blob loop relies on,
    // and emit a sphere-blob anywhere the density exceeds the humidity-
    // driven threshold.
    //
    // Why noise-anchored: each grid cell (col, row) maps to a fixed
    // noise-space anchor `((col+0.5)*cellSz, (row+0.5)*cellSz)`. The
    // noise sampled at that anchor doesn't change as the camera pans —
    // so a cell that's above threshold stays above threshold, and its
    // blob's identity (radius, z-jitter, jitter offset) is stable
    // frame-to-frame. The blob's *sprite-local* position scrolls with
    // the camera (via `lx = anchorX - noiseOffset.x`), but it doesn't
    // pop in or out at viewport edges.
    //
    // (Sprite-local anchoring — the previous approach — would index
    // cells relative to the camera, so cell (5, 3) sampled different
    // noise content each frame and blobs popped on/off as cells crossed
    // the threshold independently.)
    void GenerateBlobs(float threshold, Vector2 noiseOffset) {
        float halfW  = (textureSize.x * 0.5f) / pixelsPerUnit;
        float halfH  = bandHalfHeight;       // only walk inside the band envelope

        // Head-start curve normalizer (hoisted — constant per bake). Maps a
        // uniform roll u∈[0,1] through (exp(u·k)−1)/(exp(k)−1): a convex,
        // right-skewed curve so most cells get ~0 head start and a few roll high.
        float jk     = Mathf.Max(0.01f, densityJitterSkew);
        float jkNorm = 1f / (Mathf.Exp(jk) - 1f);
        // Hard floor (matches the [Min] on blobCellSize): a near-zero cell
        // size makes the col/row grid span millions of cells per bake and
        // hangs the editor. Guards code paths that bypass the inspector.
        float cellSz = Mathf.Max(0.2f, blobCellSize);

        // Noise-space x range covered by the viewport: a sprite-local x
        // of -halfW..+halfW maps to noise-anchor x of -halfW+nOff.x..+halfW+nOff.x.
        // Pad by blobRadiusMax so partially-visible blobs at the edges
        // still spawn (their bodies poke into the viewport from outside).
        float pad = blobRadiusMax;
        int colMin = Mathf.FloorToInt((-halfW - pad + noiseOffset.x) / cellSz);
        int colMax = Mathf.CeilToInt ((+halfW + pad + noiseOffset.x) / cellSz);

        // y has no camera-driven offset. Rows span the asymmetric band:
        // top side at +halfH, bottom side at -halfH * bandBottomScale
        // (typically much shallower → flat-bottomed clouds).
        int rowMin = Mathf.FloorToInt((bandCenterY - halfH * bandBottomScale) / cellSz);
        int rowMax = Mathf.CeilToInt ((bandCenterY + halfH) / cellSz);

        blobCount = 0;

        for (int row = rowMin; row <= rowMax; row++) {
            for (int col = colMin; col <= colMax; col++) {
                // Hashed jitter keyed on (col, row). Since (col, row) is
                // noise-anchored (not camera-relative), the jitter for a
                // given cell is the same every frame — blobs are
                // deterministic in noise space.
                float jx = Hash2D(col * 7.31f,  row * 11.17f) - 0.5f;
                float jy = Hash2D(col * 13.91f, row * 17.83f) - 0.5f;

                // Cell anchor in noise-space coordinates (the same units
                // ValueNoise is sampled in, pre-noiseScale).
                float anchorX = (col + 0.5f + jx * blobJitter) * cellSz;
                float anchorY = (row + 0.5f + jy * blobJitter) * cellSz;

                // Blob sprite-local y = its offset from the band centre.
                // The sprite itself moves with parallax (worldLockingY
                // drives spriteY in LateUpdate); blobs stay anchored to
                // the band relative to the sprite, so they ride along
                // with the parallax motion.
                //
                // Asymmetric band: full halfH above the centre, only
                // (halfH × bandBottomScale) below — clouds spawn close
                // to bandCenterY along their bottom edge but rise much
                // further above it, producing the flat-base cumulus
                // silhouette.
                float lyBand     = anchorY - bandCenterY;
                float halfHere   = (lyBand >= 0f) ? halfH : halfH * bandBottomScale;
                if (Mathf.Abs(lyBand) > halfHere) continue;

                // Sprite-local x. Shader's blob loop reads x in
                // sprite-local coords:
                //   anchorX = lx + noiseOffset.x  →  lx = anchorX - noiseOffset.x
                float lx = anchorX - noiseOffset.x;

                // Band envelope — quadratic falloff using whichever half-
                // height applies on this side of the centre.
                float bandDist = Mathf.Abs(lyBand) / halfHere;
                float band     = Mathf.Clamp01(1f - bandDist * bandDist);
                if (band <= 0f) continue;

                // Noise sample at the anchored point. noiseAspect
                // divides the x scale so features stretch horizontally —
                // at aspect=2 the x noise frequency is half, so cloud
                // clusters end up about twice as wide as they are tall.
                // The third (z) axis is `evolutionOffset` — a slowly-
                // growing time offset that lets the sampled pattern
                // smoothly morph through 3D noise space, so clouds form
                // and dissolve in place rather than just translating.
                float coarse = ValueNoise3D(anchorX * noiseScale / noiseAspect,
                                            anchorY * noiseScale,
                                            evolutionOffset);

                // Per-cell density head start — INDEPENDENT per cell, smooth in
                // time. Hash3D(col, row, ·) is uncorrelated cell-to-cell; lerping
                // between integer time steps lets each cell's roll drift slowly
                // without the discrete steps flickering.
                float tz   = evolutionOffset * densityJitterRate;
                float ti   = Mathf.Floor(tz);
                float tf   = tz - ti;
                float tu   = tf * tf * (3f - 2f * tf);   // smoothstep ease in time
                float u    = Mathf.Lerp(Hash3D(col, row, ti),
                                        Hash3D(col, row, ti + 1f), tu);

                // Right-skew the uniform roll into a one-sided head start: most
                // cells get ~0 (cross the threshold together at the base time),
                // a FEW roll high and cross EARLY — popping in as dominant lobes
                // the others grow out from, instead of an even dotted line of
                // same-size blobs. densityJitterSkew sets how rare the early
                // ones are. The base density gradient still shapes the overall
                // edge, so this breaks up the line rather than dithering it.
                float headStart = (Mathf.Exp(u * jk) - 1f) * jkNorm;
                float d = (coarse + headStart * densityJitter) * band;
                float excess = d - threshold;
                if (excess <= 0f) continue;

                // Density → max radius: how-far-above-threshold maps into
                // [blobRadiusMin, blobRadiusMax], saturating at excessForMaxSize.
                float maxR = Mathf.Lerp(blobRadiusMin, blobRadiusMax,
                                        Mathf.Clamp01(excess / Mathf.Max(0.001f, excessForMaxSize)));

                // Radius fade-in over fadeRange, shaped by fadeExponent.
                // exponent < 1 front-loads the growth (a low-excess fringe cell
                // jumps to a substantial size quickly), so blobs spend little
                // time tiny — fringe blobs reach a size that OVERLAPS neighbours
                // and merges, instead of scattering as a field of disconnected
                // specks at the cloud boundary. The steeper final shrink that
                // front-loading implies is harmless: the sub-pixel-ish cull
                // below trims the blob before the abrupt part shows.
                float t      = Mathf.Clamp01(excess / fadeRange);
                float fadeIn = Mathf.Pow(t, fadeExponent);

                // Per-cell random size factor in [0.5, 1]. Cells in a
                // high-noise cluster all see similar maxR, so without
                // this they'd spawn a row of near-identical big lobes.
                // Multiplying by a deterministic per-cell hash spreads
                // sizes between half and full maxR — a fractal-flavoured
                // mix of big and small puffs scattered through the same
                // cluster, instead of all-at-maxR. The 0.5 floor (vs a
                // raw [0,1] hash) stops blobs collapsing far below the
                // cell spacing. Hashed on (col, row) so the size is
                // stable frame-to-frame.
                float sizeRand = 0.5f + 0.5f * Hash2D(col * 47.3f, row * 31.7f);
                float radius   = maxR * fadeIn * sizeRand;

                // Cull blobs below minBlobPixels diameter. As a cell nears the
                // spawn threshold its radius fades toward 0; culling near the
                // pixel scale means the vanish is ~invisible (the key anti-blink
                // fix — the old cull at 0.2·blobRadiusMin ~4.5px chopped off a
                // STILL-VISIBLE disc, and clusters hitting that cutoff together
                // read as the cloud "blinking"). Raising minBlobPixels trims more
                // boundary specks for a small pop. Resolution-aware via pixelsPerUnit.
                if (radius * 2f * pixelsPerUnit < minBlobPixels) continue;

                float z = (Hash2D(col * 5.19f, row * 9.71f) - 0.5f) * blobDepthRange;
                // Per-blob aspect: ±20% around the inspector value,
                // hashed per cell so adjacent blobs don't all look
                // like identical horizontally-stretched ovals.
                float aspectVar = (Hash2D(col * 5.71f, row * 3.13f) - 0.5f) * 0.4f;
                float aspect    = blobAspect * (1f + aspectVar);

                blobAspectBuffer[blobCount] = aspect;
                blobBuffer[blobCount++]     = new Vector4(lx, lyBand, z, radius);
                // Cap is a safety net, not a normal operating point. Warn (not
                // silently truncate) so a genuinely over-dense sky is visible —
                // truncation reshuffles which blobs make the cut as the field
                // drifts, which reads as blinking.
                if (blobCount >= MAX_BLOBS) {
                    if (Time.unscaledTime >= nextBlobCapWarnTime) {
                        nextBlobCapWarnTime = Time.unscaledTime + 5f;
                        Debug.LogWarning($"CloudLayer: blob demand hit MAX_BLOBS ({MAX_BLOBS}); field truncated. " +
                                         "Raise MAX_BLOBS (+ shader #define) or increase blobCellSize.");
                    }
                    return;
                }
            }
        }
    }

    // ── Noise primitives (CPU side) ─────────────────────────────────
    // Hash2D is used for per-cell deterministic jitter (blob position,
    // radius, depth, aspect). ValueNoise3D samples the blob-density
    // field, with the third axis driven by evolutionOffset so the
    // sampled pattern morphs through time. These don't need to stay in
    // sync with the shader's noise (the shader only consumes already-
    // spawned blobs via the _Blobs array — it doesn't sample the
    // density field per-pixel).

    static float Frac(float x) {
        return x - Mathf.Floor(x);
    }

    static float Hash2D(float px, float py) {
        float ax = Frac(px * 123.34f);
        float ay = Frac(py * 456.21f);
        float d  = ax * (ax + 45.32f) + ay * (ay + 45.32f);
        return Frac((ax + d) * (ay + d));
    }

    static float Hash3D(float px, float py, float pz) {
        float ax = Frac(px * 123.34f);
        float ay = Frac(py * 456.21f);
        float az = Frac(pz * 789.43f);
        float d  = ax * (ax + 45.32f) + ay * (ay + 45.32f) + az * (az + 45.32f);
        return Frac((ax + d) * (ay + d) * (az + d));
    }

    // Trilinear-interpolated value noise. Drop-in 3D extension of the
    // 2D variant — Quilez-style smoothstep fade on each axis.
    static float ValueNoise3D(float px, float py, float pz) {
        float ix = Mathf.Floor(px), iy = Mathf.Floor(py), iz = Mathf.Floor(pz);
        float fx = px - ix,         fy = py - iy,         fz = pz - iz;
        float h000 = Hash3D(ix,     iy,     iz);
        float h100 = Hash3D(ix + 1, iy,     iz);
        float h010 = Hash3D(ix,     iy + 1, iz);
        float h110 = Hash3D(ix + 1, iy + 1, iz);
        float h001 = Hash3D(ix,     iy,     iz + 1);
        float h101 = Hash3D(ix + 1, iy,     iz + 1);
        float h011 = Hash3D(ix,     iy + 1, iz + 1);
        float h111 = Hash3D(ix + 1, iy + 1, iz + 1);
        float ux = fx * fx * (3f - 2f * fx);
        float uy = fy * fy * (3f - 2f * fy);
        float uz = fz * fz * (3f - 2f * fz);
        float x00 = Mathf.Lerp(h000, h100, ux);
        float x10 = Mathf.Lerp(h010, h110, ux);
        float x01 = Mathf.Lerp(h001, h101, ux);
        float x11 = Mathf.Lerp(h011, h111, ux);
        float y0  = Mathf.Lerp(x00, x10, uy);
        float y1  = Mathf.Lerp(x01, x11, uy);
        return Mathf.Lerp(y0, y1, uz);
    }
}
