using Unity.Profiling;
using UnityEngine;

// Parallax background layer. A single SpriteRenderer behind the clouds
// that displays a user-supplied tileable texture with configurable
// horizontal/vertical parallax. Sits at sortingOrder = -75 by default —
// between the sky gradient (-100) and the stars (-50), behind the cloud
// layer (0).
//
// ── How it works ───────────────────────────────────────────────────────
// Each frame BackgroundLayer.cs:
//   1. Bakes a parallax-shifted view of the user texture into a
//      viewport-shaped RenderTexture (`bgRT`) via Graphics.Blit through
//      `Hidden/BackgroundLayerGen`. The gen shader computes each RT
//      pixel's world position, applies parallax + texture-scale math,
//      and samples the user texture (which the texture importer must
//      have wrap-mode=Repeat for horizontal tiling).
//   2. Binds `bgRT` as `_MainTex` on the SpriteRenderer via MPB. The
//      visible main pass and the lighting pipeline's NormalsCapture
//      override both sample this RT at the sprite's native UVs, so
//      they see the *same* parallax-adjusted image — no alpha-mask
//      mismatch between rendering and lighting.
//
// The "bake into RT then sample at native UVs" pattern (same one
// CloudLayer uses) is critical because the NormalsCapture override
// material samples _MainTex at the sprite's NATIVE UVs, not at any
// custom world-coord UVs we'd want. Without baking, the lightmap's
// sprite mask shows the painting "as if it had no parallax" while the
// visible scene shows it parallax-shifted — producing the "ghost
// mountains in the sky" artifact at times when the lightmap and
// skyLightColor diverge (most visibly at sunset/sunrise).
//
// ── Parallax convention ────────────────────────────────────────────────
// Matches CloudLayer:
//   worldLockingX = 0 → sky-locked (texture stays glued to viewport,
//                       no apparent motion as the camera pans).
//   worldLockingX = 1 → world-locked (you walk past the texture, full
//                       parallax, foreground moves identically).
//   0.1 = canonical distant background.
//
// ── Lighting integration ───────────────────────────────────────────────
// MPB-binds a flat 1×1 normal map so NormalsCapture sees a uniform
// camera-facing normal for every painted-mountain pixel — LightSun
// then contributes a constant brightness across the visible painting
// instead of spurious per-pixel sun shading. The day/night cycle dims
// the background via the LightComposite multiply.
//
// ── Texture import settings ────────────────────────────────────────────
// User texture: Wrap Mode = Repeat on the U (horizontal) axis at a
// minimum so the painting tiles. Vertical wrap doesn't matter — the
// gen shader clips uv.y outside [0, 1] so the texture appears once
// at its natural altitude and disappears above / below. Filter Mode =
// Point for crisp pixel art.
[DefaultExecutionOrder(100)]
public class BackgroundLayer : SkyLayerBase {
    [Header("Texture")]
    [Tooltip("Tileable background sprite. Set Wrap Mode U = Repeat in the texture importer for horizontal tiling.")]
    public Texture2D texture;
    [Tooltip("Pixels-per-world-unit for the texture. Controls how big the painted content appears in the world (independent of the texture's import PPU).")]
    public float texturePixelsPerUnit = 16f;

    [Header("Position")]
    [Tooltip("World-y the texture's vertical centre aligns to when worldLockingY = 1. With <1 parallax the actual visible position drifts with the camera.")]
    public float bandCenterY = 8f;
    [Tooltip("World-z of the sprite. Must sit between SkyCamera's near (0.3) and far planes, and be larger than the cloud layer's renderZ so the background draws behind clouds. Default 6 puts it just behind the cloud band (renderZ=5).")]
    public float renderZ = 6f;

    [Header("Parallax")]
    [Tooltip("Horizontal parallax. 0 = sky-locked (texture glued to viewport, no apparent motion); 1 = world-locked (full parallax, foreground moves identically). 0.1 = canonical distant background.")]
    [Range(0f, 1f)] public float worldLockingX = 0.1f;
    [Tooltip("Vertical parallax. Same convention as worldLockingX.")]
    [Range(0f, 1f)] public float worldLockingY = 0.1f;

    [Header("Tint")]
    [Tooltip("Multiplied into the texture. Useful for day/night tinting (e.g., desaturate at dusk). Day/night brightness is already handled by the LightSun composite, so leave at white unless you want extra colour shift.")]
    public Color tint = Color.white;

    [Header("Pan buffer")]
    [Tooltip("Extra world-unit padding baked beyond the visible viewport on each side. Without this, fast camera pans expose un-baked area between 15Hz refreshes (the sprite, shifted by parallax-compensation, no longer covers the viewport edge). 2 wu handles typical fast pans; raise if you see edge gaps. Larger = a bit more GPU per bake.")]
    [Range(0f, 8f)] public float panBuffer = 2f;

    [Header("Render order")]
    [Tooltip("Sorting order within the Background sorting layer. -100 = sky gradient, -50 = stars, 0 = clouds. -75 puts the background between stars and clouds (in front of stars).")]
    public int sortingOrder = -75;

    [Header("Cloud shadows")]
    [Tooltip("How much to darken the hill painting where a cloud-shadow noise blob falls. 0 = no shadows, ~0.2 = subtle, ~0.5 = obvious dark patches. Multiplied by the painting's alpha so transparent / sky areas aren't shadowed.")]
    [Range(0f, 1f)] public float shadowStrength = 0.25f;
    [Tooltip("Frequency of the shadow noise field (1 / world-units). Lower = bigger, broader shadow blobs; higher = smaller, more numerous patches. ~0.15 gives shadows ~6-7 world units across.")]
    public float shadowNoiseScale = 0.15f;
    [Tooltip("Horizontal stretch of shadow blobs. 1 = round; 2 = twice as wide as tall (cloud shadows elongated by wind). Matches noiseAspect's purpose for the cloud blobs themselves.")]
    [Range(1f, 4f)] public float shadowAspect = 2f;
    [Tooltip("Softness of the shadow edge (half-width of the smoothstep around the cloud-coverage threshold). 0 = razor-sharp shadows; ~0.1 = soft fuzzy edges.")]
    [Range(0f, 0.3f)] public float shadowSoftness = 0.08f;


    SpriteRenderer sr;
    Material bgGenMat;
    MaterialPropertyBlock mpb;
    // Dummy 64×64 Texture2D used only to back Sprite.Create — `bgRT` is
    // what actually gets sampled at render time (MPB-bound below).
    Texture2D spriteTex;
    // Parallax-baked view of the user texture. Re-Blitted each frame
    // through Hidden/BackgroundLayerGen with the current camera + parallax
    // params. Both the visible sprite pass AND the NormalsCapture
    // override material sample this RT at native UVs — so their alpha
    // masks agree and the day/night composite doesn't ghost the
    // un-parallaxed painting into the sky.
    RenderTexture bgRT;

    static readonly int MainTexId           = Shader.PropertyToID("_MainTex");
    static readonly int NormalMapId         = Shader.PropertyToID("_NormalMap");
    static readonly int ViewportSizeId      = Shader.PropertyToID("_ViewportSize");
    static readonly int CameraPosId         = Shader.PropertyToID("_CameraPos");
    static readonly int BandCenterYId       = Shader.PropertyToID("_BandCenterY");
    static readonly int ParallaxOffsetId    = Shader.PropertyToID("_ParallaxOffset");
    static readonly int TexUVScaleId        = Shader.PropertyToID("_TexUVScale");
    static readonly int ShadowStrengthId    = Shader.PropertyToID("_ShadowStrength");
    static readonly int ShadowNoiseScaleId  = Shader.PropertyToID("_ShadowNoiseScale");
    static readonly int ShadowAspectId      = Shader.PropertyToID("_ShadowAspect");
    static readonly int ShadowSoftnessId    = Shader.PropertyToID("_ShadowSoftness");

    // BackgroundLayer normalises its own GO onto the Sky layer in case
    // the inspector / a reparent knocked it off (sprite would fall into
    // a culling gap between Main and SkyCamera otherwise).
    protected override bool ManageSkyLayer => true;

    protected override void BuildContents() {
        if (texture == null) {
            Debug.LogError("BackgroundLayer: no texture assigned — disabling.");
            enabled = false;
            return;
        }

        // The parallax-baked RT — both visible and lighting passes sample
        // this at native UV. Sized to match the camera's actual pixel
        // dimensions (re-sized in DoLateUpdate if those change) so each
        // RT pixel corresponds to a screen pixel — no downsampling.
        // Linear (sRGB=false) for consistency with the cloud's RTs.
        bgRT = MakeBgRT(Mathf.Max(1, bgCam.pixelWidth), Mathf.Max(1, bgCam.pixelHeight));

        // 64×64 dummy at PPU=64 → the sprite's native size is 1×1 world
        // units. Each frame we transform-scale the sprite to viewport
        // size, so this size is incidental; FullRect mesh ensures the
        // quad has triangles even if the dummy alpha is empty.
        spriteTex = new Texture2D(64, 64, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point,
            hideFlags  = HideFlags.HideAndDontSave,
        };
        var sprite = Sprite.Create(spriteTex, new Rect(0, 0, 64, 64),
                                   new Vector2(0.5f, 0.5f), 64f,
                                   extrude: 0, meshType: SpriteMeshType.FullRect);

        Shader genSh = Resources.Load<Shader>("Shaders/BackgroundLayerGen");
        if (genSh == null) {
            Debug.LogError("BackgroundLayer: missing shader Resources/Shaders/BackgroundLayerGen.shader — disabling.");
            enabled = false;
            return;
        }
        bgGenMat = new Material(genSh) { hideFlags = HideFlags.HideAndDontSave };

        var srGo = new GameObject("BackgroundLayerSprite");
        srGo.transform.SetParent(transform, worldPositionStays: false);
        srGo.layer = gameObject.layer;
        // Route through SpriteMaterialUtil → Resources/Materials/Sprite.mat
        // (Custom/Sprite) — the same lit-sprite material the cloud / star /
        // haze layers use, and which their identical MPB _MainTex=RT bind
        // (below) relies on. A bespoke Hidden/BackgroundLayer shader used to
        // back this SR, but under URP 17 (Unity 6) its SpriteRenderer MPB
        // _MainTex override silently stopped taking effect — the sprite
        // sampled its transparent dummy texture, so the whole layer rendered
        // invisible. Custom/Sprite honours the per-renderer _MainTex bind.
        // The parallax bake still lives in bgGenMat (Hidden/BackgroundLayerGen).
        sr = SpriteMaterialUtil.AddSpriteRenderer(srGo);
        sr.sprite = sprite;
        sr.sortingLayerName = "Background";
        sr.sortingOrder = sortingOrder;

        // MPB-bind the parallax-baked RT as _MainTex (overriding the
        // sprite-auto-bound spriteTex), and a flat-normal 1×1 as
        // _NormalMap. The flat normal makes NormalsCapture see a uniform
        // camera-facing normal everywhere, so LightSun adds constant
        // brightness rather than per-pixel varying sun shading.
        mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(MainTexId,   bgRT);
        mpb.SetTexture(NormalMapId, SpriteMaterialUtil.FlatNormalTex);
        sr.SetPropertyBlock(mpb);

        sr.transform.position = new Vector3(bgCam.transform.position.x, bgCam.transform.position.y, renderZ);

        // Anchor parallax-compensation snapshot to current camera so the
        // first per-frame sprite-position calc collapses to sky-lock and
        // doesn't read uninitialized (0, 0) bakedCam values.
        bakedCamX = bgCam.transform.position.x;
        bakedCamY = bgCam.transform.position.y;
    }

    static RenderTexture MakeBgRT(int w, int h) {
        var rt = new RenderTexture(w, h, depth: 0,
                                   RenderTextureFormat.ARGB32,
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
        if (bgRT != null)     { bgRT.Release(); bgRT = null; }
        // Note: sr.sharedMaterial is the shared Resources/Materials/Sprite.mat
        // (assigned via SpriteMaterialUtil) — do NOT destroy it here.
        if (bgGenMat != null) Destroy(bgGenMat);
        if (spriteTex != null) Destroy(spriteTex);
    }

    // CPU-side marker — sampled by Components/GpuStatsHUD.cs. See CloudLayer
    // for why this is CPU-only.
    public const string MarkerName = "Shonei.BackgroundLayer";
    static readonly ProfilerMarker s_marker = new(MarkerName);

    // Throttle the fullscreen Blit through BackgroundLayerGen to ~15 Hz —
    // baked content barely changes per frame (parallax + cloud-shadow drift
    // both move slowly), so regenerating at 60 Hz wastes ~3 of every 4 bakes.
    // Cheap per-frame work (sprite pos with parallax compensation, MPB
    // rebind, tint, sortingOrder sync) still runs every frame so apparent
    // parallax stays smooth between bakes. Mirrors CloudLayer's throttle.
    const float heavyUpdateInterval = 1f / 15f;
    float nextHeavyUpdateTime;
    // Snapshot of camera position at the last heavy bake. Used to compute
    // the sprite-position parallax compensation each frame — see DoLateUpdate.
    // Initialized in BuildContents.
    float bakedCamX, bakedCamY;

    protected override void DoLateUpdate() {
        using var _ = s_marker.Auto();
        Vector3 camPos = bgCam.transform.position;

        // Cover the SkyCamera's viewport regardless of zoom, with a panBuffer
        // of extra world-unit padding on each side so fast camera pans don't
        // expose un-baked edges during the 15Hz refresh interval. extW/extH
        // are the actual world extent the sprite spans; viewW/viewH are kept
        // only for the camera pixel-density ratio used to size the RT.
        float viewH = bgCam.orthographicSize * 2f;
        float viewW = viewH * bgCam.aspect;
        float extW  = viewW + 2f * panBuffer;
        float extH  = viewH + 2f * panBuffer;
        sr.transform.localScale = new Vector3(extW, extH, 1f);

        // Per-frame MPB rebind. Editor events (sprite reimport, material
        // refresh, OnValidate paths) can silently clear the SpriteRenderer's
        // MaterialPropertyBlock — at which point _MainTex falls back to the
        // dummy spriteTex. Negligible cost.
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(MainTexId,   bgRT);
        mpb.SetTexture(NormalMapId, SpriteMaterialUtil.FlatNormalTex);
        sr.SetPropertyBlock(mpb);

        // Per-frame: sortingOrder + tint (cheap, may change at runtime).
        sr.sortingOrder = sortingOrder;
        sr.color = tint;

        // ── Throttled heavy work (~15Hz) ─────────────────────────────────────
        // Unscaled time: see CloudLayer for the rationale — paused camera
        // pans must still re-bake or the sprite drifts off the viewport.
        if (Time.unscaledTime >= nextHeavyUpdateTime) {
            nextHeavyUpdateTime = Time.unscaledTime + heavyUpdateInterval;

            // Snapshot camera for parallax compensation in the next interval's
            // cheap per-frame updates. Must match what the bake below uses for
            // its parallaxOffset, or the per-frame sprite-position formula
            // disagrees with the baked content and the background visibly jumps
            // at each throttle tick.
            bakedCamX = camPos.x;
            bakedCamY = camPos.y;

            // Parallax offset: shifts the texture UV. Convention matches
            // CloudLayer — worldLocking=0 → offset tracks camera fully so
            // the UV stays constant (sky-locked: texture rides with the
            // viewport); worldLocking=1 → offset is zero so the UV equals
            // world position (world-locked: you walk past the texture).
            Vector2 parallaxOffset = new Vector2(camPos.x * (1f - worldLockingX),
                                                 camPos.y * (1f - worldLockingY));

            // Texture covers (texture.width / ppu) world units per UV tile.
            float texWorldW = texture.width  / texturePixelsPerUnit;
            float texWorldH = texture.height / texturePixelsPerUnit;
            Vector2 texUVScale = new Vector2(1f / texWorldW, 1f / texWorldH);

            bgGenMat.SetVector(ViewportSizeId,    new Vector2(extW, extH));
            bgGenMat.SetVector(CameraPosId,       new Vector2(camPos.x, camPos.y));
            bgGenMat.SetFloat (BandCenterYId,     bandCenterY);
            bgGenMat.SetVector(ParallaxOffsetId,  parallaxOffset);
            bgGenMat.SetVector(TexUVScaleId,      texUVScale);
            bgGenMat.SetFloat (ShadowStrengthId,   shadowStrength);
            bgGenMat.SetFloat (ShadowNoiseScaleId, shadowNoiseScale);
            bgGenMat.SetFloat (ShadowAspectId,     shadowAspect);
            bgGenMat.SetFloat (ShadowSoftnessId,   shadowSoftness);

            // Keep bgRT at the camera's pixel-per-world ratio so the visible sprite
            // samples it 1:1 (no upscaling blur). Scale up by the buffered/viewport
            // ratio so the extra world coverage gets proportional pixel coverage.
            int targetW = Mathf.Max(1, Mathf.RoundToInt(bgCam.pixelWidth  * (extW / viewW)));
            int targetH = Mathf.Max(1, Mathf.RoundToInt(bgCam.pixelHeight * (extH / viewH)));
            if (bgRT.width != targetW || bgRT.height != targetH) {
                bgRT.Release();
                Destroy(bgRT);
                bgRT = MakeBgRT(targetW, targetH);
                // Re-bind on the next per-frame MPB rebind (no extra work here).
            }

            if (!bgRT.IsCreated()) bgRT.Create();

            // Explicit _MainTex bind on the gen material before the Blit.
            // Graphics.Blit's source-arg binding occasionally doesn't reach a
            // custom material's TEXTURE2D(_MainTex) sampler — explicitly setting
            // it here removes the dependency on that implicit hook.
            bgGenMat.SetTexture(MainTexId, texture);
            Graphics.Blit(texture, bgRT, bgGenMat);
        }

        // ── Sprite position (always last) ──────────────────────────────────
        // Run AFTER any bake so bakedCam* in the formula reflect the just-
        // baked anchors. If positioning ran first, the bake frame would
        // render new RT content at the OLD sprite anchor — visible as a
        // 15Hz jitter while panning. Formula collapses to camPos right
        // after a bake and drifts at (1 - worldLocking) × camMotion until
        // the next bake. Mirrors CloudLayer's compensation formula.
        float spriteX = camPos.x - worldLockingX * (camPos.x - bakedCamX);
        float spriteY = camPos.y - worldLockingY * (camPos.y - bakedCamY);
        sr.transform.position = new Vector3(spriteX, spriteY, renderZ);
    }
}
