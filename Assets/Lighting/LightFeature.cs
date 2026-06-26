using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

// ScriptableRendererFeature — add this to your Universal Renderer Data asset.
// Unified lighting pass: ambient + torches use Max blend, sun is additive on top.
//
// Pipeline each frame:
//   1. NormalsCapturePass renders all sprites' _NormalMap textures into
//      _CapturedNormalsRT (world-space normals, packed 0–1).
//   2. Clear light RT to black, then blit ambient × sky exposure (SkyExposure).
//   3. Draw point LightSources (torches) as circle quads — NdotL × radial falloff.
//   4. Draw directional LightSources (sun) fullscreen — NdotL from _CapturedNormalsRT.
//      (Shadow ray march disabled for performance — see LightSun.shader.)
//   5. Draw emission contributions (sprites with _EmissionMap secondary tex)
//      additively into the light RT — flame pixels saturate to white so the
//      composite multiply preserves their painted color regardless of time of day.
//   6. Multiply-blit light RT onto scene: final = scene × lightmap.
//
// RenderGraph (URP 17 / Unity 6): both passes implement RecordRenderGraph via
// AddUnsafePass — they do manual render-target binding, interleaved global shader
// state, and many small draws, which is exactly what the unsafe pass escape hatch
// is for. The legacy OnCameraSetup/Execute path no longer runs under RenderGraph.
public class LightFeature : ScriptableRendererFeature {
    [Tooltip("Minimum NdotL floor applied to all lights — prevents back-faces from going fully black.")]
    [Range(0f, 1f)] public float ambientNormal = 0.15f; // ! set this in inspector

    // Shadow casting disabled for performance. Uncomment to re-enable:
    // [Header("Sun Shadows")]
    // [Range(0f, 5f)] public float shadowLength = 0.5f;
    // [Range(0f, 1f)] public float shadowDarkness = 0.6f;

    [Header("Underground")]
    [Tooltip("How far light (incl. point lights) reaches into solid material, in tiles. Beyond this depth, " +
             "tile/building pixels are forced to deepAmbientColor — erasing point-light contribution. Controls " +
             "sub-tile edge darkening + sky-light falloff. 0 = only the exposed surface lit; whole interior = deepAmbient.\n\n" +
             "CHANGES DON'T SHOW until you run Tools > Bake Tile Edges (Force) and reload the world — the edge " +
             "depth is baked into the tile Texture2DArrays, and the plain (non-Force) bake skips on source-art mtime, " +
             "so it won't pick up a penetration change.")]
    [Range(0f, 4f)] public float lightPenetrationDepth = 1f;
    [Tooltip("Constant ambient light in deep interiors. Not affected by time of day.")]
    public Color deepAmbientColor = new Color(0.04f, 0.04f, 0.06f, 1f);

    [Header("Composite")]
    [Tooltip("How much the sun tints empty sky/background pixels (0 = no tint, 1 = full lightmap applied).")]
    [Range(0f, 1f)] public float skyLightBlend = 1f;
    [Header("Emission")]
    [Tooltip("Global multiplier on per-pixel emission written into the lightmap by EmissionWriter.shader. " +
             "0 = no emissive glow, 1 = mask alpha drives the lightmap directly, >1 = boost. " +
             "Stacks multiplicatively with the per-LightSource MPB scale (which gates emission off when " +
             "the source isn't actually emitting light — daytime, out of fuel, disabled).")]
    [Range(0f, 2f)] public float emissionStrength = 1f;

    [Header("Sort-aware effective light height")]
    [Tooltip("Sort-bucket delta (in normalized units, 1.0 spans all 6 buckets) over " +
             "which the effective light height ramps across BEHIND receivers from +lightHeight " +
             "(at delta=0) toward +lightHeight * behindFarHeightFactor (at full range). " +
             "0.20 ≈ one bucket step (Phase 4 buckets: ~0.2 apart in normalized space). " +
             "In-front receivers always use -lightHeight (hard flip), independent of this range.")]
    [Range(0.01f, 0.5f)] public float sortRampRange = 0.20f;
    [Tooltip("Height scale at the far end of the behind ramp. 1.0 = flat ramp (uniform " +
             "behind lighting). >1 = steeper/more top-down for deep-behind receivers. " +
             "<1 = shallower/more grazing. Only affects the BEHIND branch — in-front " +
             "receivers always flip to -lightHeight so the light reads as coming from behind.")]
    [Range(0f, 4f)] public float behindFarHeightFactor = 2.0f;

    // Static accessor so TileSpriteCache (normal-map bake) and SkyExposure can read
    // the depth without needing a reference to the ScriptableRendererFeature asset.
    public static float penetrationDepth { get; private set; } = 1f;

    [Header("Normals")]
    [Tooltip("Only these layers are captured into the normals RT. Sprites on excluded layers are unlit — they cast and receive no shadows.")]
    public LayerMask litLayers = ~0; // default: Everything
    [Tooltip("Subset of litLayers that block sunlight and cast shadows. Lit-only layers (e.g. clouds) are excluded here so they receive normal-map lighting but cast no shadows.")]
    public LayerMask shadowCasterLayers = ~0; // default: Everything
    [Tooltip("Layers that receive sun + ambient only — not torch/point lights. Good for clouds and distant backgrounds. Can overlap with litLayers; directional-only always wins.")]
    public LayerMask directionalOnlyLayers = 0; // default: Nothing
    [Tooltip("Water layer — rendered into the normals RT using NormalsCaptureWater, which samples " +
             "_WaterSurfaceTex for transparency. Water is lit-only (torch + sun) but casts no shadows.")]
    public LayerMask waterLayer = 0; // set to the 'Water' layer in the inspector
    [Tooltip("Background layer — the chunked background walls (BackgroundTileMeshController). " +
             "Drawn into the normals RT with ChunkedBackgroundNormalsCapture (flat normal, " +
             "lit-only alpha 0.5, no shadow cast).")]
    public LayerMask backgroundLayer = 0; // set to the 'Background' layer in the inspector
    [Tooltip("Chunked tile layer — drawn with ChunkedNormalsCapture, which samples Texture2DArray " +
             "slices using per-vertex slice indices (see TileMeshController). Excluded from the " +
             "standard shadow-caster pass so chunked meshes aren't double-drawn into the normals RT.")]
    public LayerMask tileChunkLayer = 0; // set to the 'TileChunk' layer in the inspector

    // Inspector-assigned shader references. These exist so the lighting shaders
    // are part of the renderer asset's serialized graph — Unity then force-includes
    // them in builds. The earlier code looked them up by string via
    // CoreUtils.CreateEngineMaterial("Hidden/X"), which works in the editor but
    // gets stripped in builds (no Material asset references → build pipeline can't
    // see them used → they're dropped → CreateEngineMaterial returns a material
    // with a missing shader → silent black output / pink fallback).
    [Header("Shaders (assign in inspector — required for builds)")]
    [SerializeField] Shader normalsCaptureShader;
    [SerializeField] Shader normalsCaptureWaterShader;
    [SerializeField] Shader chunkedNormalsCaptureShader;
    [SerializeField] Shader chunkedBackgroundNormalsCaptureShader;
    [SerializeField] Shader lightCircleShader;
    [SerializeField] Shader lightSunShader;
    [SerializeField] Shader lightCompositeShader;
    [SerializeField] Shader lightAmbientFillShader;
    [SerializeField] Shader emissionWriterShader;

    NormalsCapturePass capturePass;
    LightPass          lightPass;

    public override void Create() {
        penetrationDepth = lightPenetrationDepth;
        capturePass = new NormalsCapturePass(normalsCaptureShader, normalsCaptureWaterShader, chunkedNormalsCaptureShader, chunkedBackgroundNormalsCaptureShader) {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
        };
        lightPass = new LightPass(lightCircleShader, lightSunShader, lightCompositeShader, lightAmbientFillShader, emissionWriterShader) {
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents
        };
        // LightPass declares a RenderGraph read dependency on the normals RT that
        // capturePass produces, so it needs a reference to that pass.
        lightPass.SetCapturePass(capturePass);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
        if (!Application.isPlaying) return;
        // Flat-lighting mode (SettingsManager.flatLighting): the full pipeline still
        // runs — occlusion, deep ambient, and the composite multiply stay intact — but
        // the generic normals-capture override writes flat camera-facing normals, so
        // dynamic sprites (animals/plants/buildings) get uniform, un-shaded lighting.
        // Terrain tiles keep their depth (separate chunked shader). Tolerate a missing
        // SettingsManager (defaults to shaded).
        float flatNormals = (SettingsManager.instance != null && SettingsManager.instance.flatLighting) ? 1f : 0f;
        // Interior lighting mode (SettingsManager.interiorMode). Drives two independent globals:
        // _InteriorLit promotes enclosed-building interiors from the directional-only tier (skip
        // torches) to lit-only (receive torches) in the capture pass — wall-shadows mode only;
        // _PointShadows enables the per-pixel wall ray-march in the light pass — on for everything
        // but the legacy no-shadows mode. Tolerate a missing SettingsManager (defaults to legacy off).
        var sm = SettingsManager.instance;
        float interiorLit = (sm != null && sm.interiorLit)  ? 1f : 0f;
        float pointShadows = (sm != null && sm.pointShadows) ? 1f : 0f;
        // Flood-fill (geodesic) point lighting: when on, each point light samples its per-light reach
        // field (LightReachField) for magnitude instead of radial falloff + the thickness shadow.
        float floodFill = (sm != null && sm.floodFill) ? 1f : 0f;
        // Skip cameras that see no sprites participating in the normals RT pipeline
        // (lit, directional-only, or water). The Unlit overlay camera hits this check
        // because its culling mask only contains layers excluded from all three masks.
        var cullingMask = renderingData.cameraData.camera.cullingMask;
        if (cullingMask == 0) return;
        if ((cullingMask & (litLayers | directionalOnlyLayers | waterLayer | backgroundLayer | tileChunkLayer)) == 0) return;
        capturePass.Setup(litLayers, shadowCasterLayers, directionalOnlyLayers, waterLayer, backgroundLayer, tileChunkLayer, flatNormals, interiorLit);
        renderer.EnqueuePass(capturePass);
        lightPass.Setup(ambientNormal, deepAmbientColor, skyLightBlend, sortRampRange, behindFarHeightFactor, emissionStrength, tileChunkLayer, pointShadows, floodFill);
        renderer.EnqueuePass(lightPass);
    }

    protected override void Dispose(bool disposing) {
        capturePass?.Dispose();
        lightPass?.Dispose();
    }
}

// ── Normals Capture Pass ──────────────────────────────────────────────────────
// Renders all lit sprites into _CapturedNormalsRT using Hidden/NormalsCapture.
// Must run before LightPass so the RT is populated when lighting reads it.

class NormalsCapturePass : ScriptableRenderPass, System.IDisposable {
    // Profiler markers — sampled by Components/GpuStatsHUD.cs. Keep the names
    // stable; the HUD references them by literal string.
    //
    // MarkerName is the CPU-side dispatch marker (ProfilerMarker.Auto, now around
    // RecordRenderGraph). s_gpuSampler is a CustomSampler created with
    // collectGpuData=true — REQUIRED for GPU timing to actually be captured. The
    // plain cmd.BeginSample(string) overload does NOT request GPU data; only the
    // CustomSampler overload does. Read via
    // Sampler.Get(name).GetRecorder().gpuElapsedNanoseconds.
    public const string MarkerName    = "Shonei.NormalsCapturePass";
    public const string GpuSampleName = "Shonei.NormalsCapturePass.GPU";
    static readonly ProfilerMarker s_marker    = new(MarkerName);
    static readonly CustomSampler  s_gpuSampler = CustomSampler.Create(GpuSampleName, true);

    static readonly int CapturedNormalsId = Shader.PropertyToID("_CapturedNormalsRT");
    // Global shader property — set per bucket iteration in the render func so the
    // override-material shaders (NormalsCapture, NormalsCaptureWater) write the
    // right sort bucket into the RT's B channel. Replaces the per-sprite _SortBucket
    // MPB. (Both chunked overrides instead read _SortBucket from a per-chunk MPB.)
    static readonly int SortBucketGlobalId = Shader.PropertyToID("_SortBucket");
    static readonly int FlatNormalsId      = Shader.PropertyToID("_FlatNormals");
    static readonly int InteriorLitId      = Shader.PropertyToID("_InteriorLit");

    // Camera-facing-normal + directional-only-tier clear for non-world cameras.
    //   rg = 0.5 → decoded normal (0,0,-1) camera-facing.
    //   b  = 0   → bucket 0 (unused; non-world cams skip point lights).
    //   a  = 0.3 → directional-only tier. LightComposite uses alpha > 0.25 to pick
    //              "multiply by lightmap" vs the bright daytime _SkyLightColor;
    //              without it the sky wouldn't darken at night.
    static readonly Color NonWorldClear = new Color(0.5f, 0.5f, 0f, 0.3f);

    readonly Material mat;
    readonly Material waterMat;
    readonly Material chunkedMat;
    readonly Material bgChunkedMat;
    int litMask           = ~0;
    int shadowMask        = ~0;
    int dirOnlyMask       = 0;
    int waterMask         = 0;
    int backgroundMask    = 0;
    int tileChunkMask     = 0;
    float flatNormals     = 0f;   // 1 = write flat camera-facing normals (flat-lighting mode)
    float interiorLit     = 0f;   // 1 = promote dir-only tier (0.3) to lit-only (0.5) — see _InteriorLit

    // The normals RT this pass produced this frame. LightPass reads it (declares a
    // RenderGraph read dependency) and it is also bound as the global
    // _CapturedNormalsRT for every lighting shader via SetGlobalTextureAfterPass.
    public TextureHandle NormalsHandle { get; private set; }

    public NormalsCapturePass(Shader normalsCapture, Shader normalsCaptureWater, Shader chunkedNormalsCapture, Shader chunkedBackgroundNormalsCapture) {
        if (normalsCapture == null || normalsCaptureWater == null) {
            Debug.LogError("LightFeature: NormalsCapture shader fields are unassigned — assign them in the URP Universal Renderer asset's LightFeature inspector. Lighting will be disabled.");
            return;
        }
        mat           = CoreUtils.CreateEngineMaterial(normalsCapture);
        waterMat      = CoreUtils.CreateEngineMaterial(normalsCaptureWater);
        // Chunked tile capture is optional — if the shader isn't assigned the
        // pass simply skips chunked-tile drawing. Useful while migrating to
        // the chunked renderer (renderer asset hasn't been wired yet).
        if (chunkedNormalsCapture != null)
            chunkedMat = CoreUtils.CreateEngineMaterial(chunkedNormalsCapture);
        // Background chunked capture — optional/null-guarded like chunkedMat, so the
        // pass simply skips background-chunk drawing until the shader is wired.
        if (chunkedBackgroundNormalsCapture != null)
            bgChunkedMat = CoreUtils.CreateEngineMaterial(chunkedBackgroundNormalsCapture);
    }

    public void Setup(int litMask, int shadowMask, int dirOnlyMask, int waterMask, int backgroundMask, int tileChunkMask, float flatNormals, float interiorLit) {
        this.litMask          = litMask;
        this.shadowMask       = shadowMask;
        this.dirOnlyMask      = dirOnlyMask;
        this.waterMask        = waterMask;
        this.backgroundMask   = backgroundMask;
        this.tileChunkMask    = tileChunkMask;
        this.flatNormals      = flatNormals;
        this.interiorLit      = interiorLit;
    }

    public void Dispose() {
        CoreUtils.Destroy(mat);
        CoreUtils.Destroy(waterMat);
        CoreUtils.Destroy(chunkedMat);
        CoreUtils.Destroy(bgChunkedMat);
    }

    // Per-frame data captured at record time and consumed in the render func. RG pools
    // and reuses PassData instances, so every field is reset/reassigned each frame; the
    // RendererListHandle arrays are allocated once (readonly) and reused.
    class PassData {
        public TextureHandle normals;
        public bool isWorldCam;
        public Color clearColor;
        public float flatNormals;
        public float interiorLit;
        public bool hasBackground, hasDirOnly, hasLitOnly, hasChunked, hasShadow, hasWater;
        public RendererListHandle background, chunked;
        public readonly RendererListHandle[] dirOnly = new RendererListHandle[SortBucketUtil.BucketCount];
        public readonly RendererListHandle[] litOnly = new RendererListHandle[SortBucketUtil.BucketCount];
        public readonly RendererListHandle[] shadow  = new RendererListHandle[SortBucketUtil.BucketCount];
        public readonly RendererListHandle[] water   = new RendererListHandle[SortBucketUtil.BucketCount];
        public readonly float[] bucketNorm = new float[SortBucketUtil.BucketCount];
    }

    // One RendererList per tier draw (replaces a legacy context.DrawRenderers call).
    // renderingLayerMask == uint.MaxValue means "all buckets" — used by the background
    // and chunked-tile draws, which don't loop per bucket.
    static RendererListHandle MakeList(RenderGraph rg, CullingResults cull, Camera cam,
                                       Material overrideMat, int passIndex, int layerMask, uint renderingLayerMask) {
        var sort = new SortingSettings(cam) { criteria = SortingCriteria.CommonTransparent };
        var draw = new DrawingSettings(new ShaderTagId("Universal2D"), sort) {
            overrideMaterial = overrideMat, overrideMaterialPassIndex = passIndex
        };
        var filter = new FilteringSettings(RenderQueueRange.all, layerMask) { renderingLayerMask = renderingLayerMask };
        return rg.CreateRendererList(new RendererListParams(cull, draw, filter));
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
        if (mat == null) return;
        using var _ = s_marker.Auto();
        var renderingData = frameData.Get<UniversalRenderingData>();
        var cameraData    = frameData.Get<UniversalCameraData>();
        var cam  = cameraData.camera;
        var cull = renderingData.cullResults;

        // ARGB32 — the lighting tier system encodes shadow/lit/dirOnly in alpha
        // (1.0/0.5/0.3); the camera's default HDR format has no alpha. depthBufferBits 0
        // because the normals RT is colour-only. CreateRenderGraphTexture is internal to
        // URP, so build the descriptor and use the public renderGraph.CreateTexture.
        var rtDesc = cameraData.cameraTargetDescriptor;
        rtDesc.depthBufferBits = 0;
        rtDesc.colorFormat = RenderTextureFormat.ARGB32;
        NormalsHandle = renderGraph.CreateTexture(new TextureDesc(rtDesc) {
            name = "_CapturedNormalsRT", filterMode = FilterMode.Point, clearBuffer = false
        });

        bool isWorldCam = (cam.cullingMask & tileChunkMask) != 0;

        using (var builder = renderGraph.AddUnsafePass<PassData>(MarkerName, out var data)) {
            data.normals       = NormalsHandle;
            data.isWorldCam    = isWorldCam;
            data.clearColor    = isWorldCam ? Color.clear : NonWorldClear;
            data.flatNormals   = flatNormals;
            data.interiorLit   = interiorLit;
            data.hasBackground = data.hasDirOnly = data.hasLitOnly = false;
            data.hasChunked    = data.hasShadow  = data.hasWater   = false;
            for (int b = 0; b < SortBucketUtil.BucketCount; b++)
                data.bucketNorm[b] = SortBucketUtil.BucketToNormalized(b);

            builder.UseTexture(data.normals, AccessFlags.Write);
            builder.AllowGlobalStateModification(true);
            // Bind as the global _CapturedNormalsRT for the light shaders (the old
            // GetTemporaryRT nameID did this implicitly). Also keeps the producing pass
            // alive across RenderGraph culling.
            builder.SetGlobalTextureAfterPass(data.normals, CapturedNormalsId);

            // Build the RendererLists. Tier order is load-bearing — later draws overwrite
            // earlier ones in the normals RT: background → dirOnly → litOnly → shadow
            // casters → water → chunked tiles. Chunked tiles (bodies/grass/snow) draw LAST
            // because they sit at sortingOrder 74-81 — in front of mice + water in the
            // color pass — so their normals must win on overlap too (else a mouse behind a
            // tile gets its shape shaded into the dirt). They still draw after background,
            // so background's lit-only alpha is overwritten by the chunked shadow-caster
            // alpha and underground edge-depth darkening survives.
            // Chunk layers use dedicated array-sampling overrides, so exclude them from
            // the generic lit/shadow tiers; background reuses the Background layer.
            if (isWorldCam) {
                int litOnlyMask    = litMask & ~shadowMask & ~dirOnlyMask & ~backgroundMask;
                int shadowOnlyMask = litMask & shadowMask & ~dirOnlyMask & ~tileChunkMask & ~backgroundMask;

                if (backgroundMask != 0 && bgChunkedMat != null) {
                    data.background = MakeList(renderGraph, cull, cam, bgChunkedMat, 0, backgroundMask, uint.MaxValue);
                    data.hasBackground = true;
                    builder.UseRendererList(data.background);
                }
                if (dirOnlyMask != 0) {
                    data.hasDirOnly = true;
                    for (int b = 0; b < SortBucketUtil.BucketCount; b++) {
                        data.dirOnly[b] = MakeList(renderGraph, cull, cam, mat, 2, dirOnlyMask, 1u << b);
                        builder.UseRendererList(data.dirOnly[b]);
                    }
                }
                if (litOnlyMask != 0) {
                    data.hasLitOnly = true;
                    for (int b = 0; b < SortBucketUtil.BucketCount; b++) {
                        data.litOnly[b] = MakeList(renderGraph, cull, cam, mat, 1, litOnlyMask, 1u << b);
                        builder.UseRendererList(data.litOnly[b]);
                    }
                }
                if (shadowOnlyMask != 0) {
                    data.hasShadow = true;
                    for (int b = 0; b < SortBucketUtil.BucketCount; b++) {
                        data.shadow[b] = MakeList(renderGraph, cull, cam, mat, 0, shadowOnlyMask, 1u << b);
                        builder.UseRendererList(data.shadow[b]);
                    }
                }
                if (waterMask != 0 && waterMat != null) {
                    data.hasWater = true;
                    for (int b = 0; b < SortBucketUtil.BucketCount; b++) {
                        data.water[b] = MakeList(renderGraph, cull, cam, waterMat, 1, waterMask, 1u << b);
                        builder.UseRendererList(data.water[b]);
                    }
                }
                // Chunked tiles built last so the draw-order list mirrors the render func.
                if (tileChunkMask != 0 && chunkedMat != null) {
                    data.chunked = MakeList(renderGraph, cull, cam, chunkedMat, 0, tileChunkMask, uint.MaxValue);
                    data.hasChunked = true;
                    builder.UseRendererList(data.chunked);
                }
            }

            builder.SetRenderFunc((PassData d, UnsafeGraphContext context) => {
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                cmd.BeginSample(s_gpuSampler);
                cmd.SetRenderTarget(d.normals);
                // Drives the generic override's flat-normal branch; the chunked / water
                // overrides don't declare _FlatNormals, so terrain keeps its depth.
                cmd.SetGlobalFloat(FlatNormalsId, d.flatNormals);
                // Drives the generic override's dir-only→lit-only promotion (interior point lighting).
                cmd.SetGlobalFloat(InteriorLitId, d.interiorLit);
                cmd.ClearRenderTarget(false, true, d.clearColor);

                // Non-world cameras (SkyCamera etc.) only need the cleared tier values —
                // skip the whole per-sprite DrawRenderers workload.
                if (d.isWorldCam) {
                    if (d.hasBackground) cmd.DrawRendererList(d.background);
                    // Loop A: dirOnly + litOnly tiers. _SortBucket global set per bucket;
                    // the override shaders read it (no per-sprite MPB).
                    for (int b = 0; b < SortBucketUtil.BucketCount; b++) {
                        cmd.SetGlobalFloat(SortBucketGlobalId, d.bucketNorm[b]);
                        if (d.hasDirOnly) cmd.DrawRendererList(d.dirOnly[b]);
                        if (d.hasLitOnly) cmd.DrawRendererList(d.litOnly[b]);
                    }
                    // Loop B: shadow casters + water.
                    for (int b = 0; b < SortBucketUtil.BucketCount; b++) {
                        cmd.SetGlobalFloat(SortBucketGlobalId, d.bucketNorm[b]);
                        if (d.hasShadow) cmd.DrawRendererList(d.shadow[b]);
                        if (d.hasWater)  cmd.DrawRendererList(d.water[b]);
                    }
                    // Chunked tiles LAST (carry _SortBucket per chunk via MPB, so the
                    // bucket-loop global above doesn't reach them). Tile bodies/grass/snow
                    // sort at 74-81 — in front of mice + water in the color pass — so their
                    // normals must overwrite those sprites on overlap. Still after the
                    // background draw, so underground edge-depth darkening holds.
                    if (d.hasChunked) cmd.DrawRendererList(d.chunked);
                }
                cmd.EndSample(s_gpuSampler);
            });
        }
    }
}

// ── Light Pass ────────────────────────────────────────────────────────────────

class LightPass : ScriptableRenderPass, System.IDisposable {
    // Profiler markers — sampled by Components/GpuStatsHUD.cs. Keep the names
    // stable; the HUD references them by literal string.
    // See NormalsCapturePass for the CPU vs GPU marker pattern explanation.
    public const string MarkerName    = "Shonei.LightPass";
    public const string GpuSampleName = "Shonei.LightPass.GPU";
    static readonly ProfilerMarker s_marker     = new(MarkerName);
    static readonly CustomSampler  s_gpuSampler = CustomSampler.Create(GpuSampleName, true);

    float ambientNormal;
    float skyLightBlend;
    float sortRampRange;
    float behindFarHeightFactor;
    float emissionStrength = 1f;
    float pointShadows = 0f;   // 1 = ray-march point lights against solid tiles (see _PointShadows)
    float floodFill = 0f;      // 1 = geodesic flood-fill point lighting (see _FloodFill / LightReachField)
    int   tileChunkMask;
    Color deepAmbientColor;
    NormalsCapturePass capture;               // for the normals RT handle (set by LightFeature.Create)
    readonly Material circleMat;
    readonly Material sunMat;
    readonly Material compositeMat;
    readonly Material ambientFillMat;
    readonly Material emissionMat;
    readonly Mesh     circleMesh;
    readonly MaterialPropertyBlock mpb = new();

    public LightPass(Shader lightCircle, Shader lightSun, Shader lightComposite, Shader lightAmbientFill, Shader emissionWriter) {
        if (lightCircle == null || lightSun == null || lightComposite == null || lightAmbientFill == null || emissionWriter == null) {
            Debug.LogError("LightFeature: Light* shader fields are unassigned — assign them in the URP Universal Renderer asset's LightFeature inspector. Lighting will be disabled.");
            circleMesh = CreateOctagon();
            return;
        }
        circleMat       = CoreUtils.CreateEngineMaterial(lightCircle);
        sunMat          = CoreUtils.CreateEngineMaterial(lightSun);
        compositeMat    = CoreUtils.CreateEngineMaterial(lightComposite);
        ambientFillMat  = CoreUtils.CreateEngineMaterial(lightAmbientFill);
        emissionMat     = CoreUtils.CreateEngineMaterial(emissionWriter);
        circleMesh      = CreateOctagon();
    }

    public void Dispose() {
        CoreUtils.Destroy(circleMat);
        CoreUtils.Destroy(sunMat);
        CoreUtils.Destroy(compositeMat);
        CoreUtils.Destroy(ambientFillMat);
        CoreUtils.Destroy(emissionMat);
        CoreUtils.Destroy(circleMesh);
    }

    public void Setup(float ambientNormal, Color deepAmbientColor, float skyLightBlend, float sortRampRange, float behindFarHeightFactor, float emissionStrength, int tileChunkMask, float pointShadows, float floodFill) {
        this.ambientNormal         = ambientNormal;
        this.deepAmbientColor      = deepAmbientColor;
        this.skyLightBlend         = skyLightBlend;
        this.sortRampRange         = sortRampRange;
        this.behindFarHeightFactor = behindFarHeightFactor;
        this.emissionStrength      = emissionStrength;
        this.tileChunkMask         = tileChunkMask;
        this.pointShadows          = pointShadows;
        this.floodFill             = floodFill;
    }

    // Set by LightFeature.Create. RecordRenderGraph reads capture.NormalsHandle to
    // declare a read dependency so RenderGraph runs the capture pass first.
    public void SetCapturePass(NormalsCapturePass capture) {
        this.capture = capture;
    }

    class PassData {
        public TextureHandle lightRT, camColor, normals;
        public Material circleMat, sunMat, compositeMat, ambientFillMat, emissionMat;
        public Mesh circleMesh;
        public MaterialPropertyBlock mpb;
        public float ambientNormal, skyLightBlend, sortRampRange, behindFarHeightFactor, emissionStrength;
        public float pointShadows;
        public float floodFill;
        public Color deepAmbientColor;
        public int tileChunkMask;
        public Camera cam;
        public Matrix4x4 view, proj;
        public bool isWorldCam;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
        if (circleMat == null || sunMat == null || compositeMat == null) return;
        using var _ = s_marker.Auto();
        var resourceData = frameData.Get<UniversalResourceData>();
        var cameraData   = frameData.Get<UniversalCameraData>();
        var cam = cameraData.camera;

        // Light RT: full camera-target format, colour-only, bilinear. Created via the
        // public renderGraph.CreateTexture (CreateRenderGraphTexture is URP-internal).
        var rtDesc = cameraData.cameraTargetDescriptor;
        rtDesc.depthBufferBits = 0;
        var lightRT = renderGraph.CreateTexture(new TextureDesc(rtDesc) {
            name = "_CustomLightRT", filterMode = FilterMode.Bilinear, clearBuffer = false
        });

        using (var builder = renderGraph.AddUnsafePass<PassData>(MarkerName, out var data)) {
            data.lightRT       = lightRT;
            data.camColor      = resourceData.activeColorTexture;
            data.normals       = capture != null ? capture.NormalsHandle : default;
            data.circleMat     = circleMat;
            data.sunMat        = sunMat;
            data.compositeMat  = compositeMat;
            data.ambientFillMat = ambientFillMat;
            data.emissionMat   = emissionMat;
            data.circleMesh    = circleMesh;
            data.mpb           = mpb;
            data.ambientNormal = ambientNormal;
            data.skyLightBlend = skyLightBlend;
            data.sortRampRange = sortRampRange;
            data.behindFarHeightFactor = behindFarHeightFactor;
            data.emissionStrength = emissionStrength;
            data.pointShadows  = pointShadows;
            data.floodFill     = floodFill;
            data.deepAmbientColor = deepAmbientColor;
            data.tileChunkMask = tileChunkMask;
            data.cam           = cam;
            data.view          = cameraData.GetViewMatrix();
            data.proj          = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), renderIntoTexture: false);
            data.isWorldCam    = (cam.cullingMask & tileChunkMask) != 0;

            builder.UseTexture(data.lightRT, AccessFlags.ReadWrite);
            // Composite multiplies the lightmap into the scene via a hardware DstColor
            // blend, so we WRITE the camera colour (the blend reads the existing
            // framebuffer content — no scene-copy texture needed). ReadWrite preserves it.
            builder.UseTexture(data.camColor, AccessFlags.ReadWrite);
            // Read dependency on the normals RT so RenderGraph runs the capture pass first.
            if (data.normals.IsValid()) builder.UseTexture(data.normals, AccessFlags.Read);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((PassData d, UnsafeGraphContext context) => {
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                cmd.BeginSample(s_gpuSampler);

                // TextureHandle implicitly converts to both Texture and
                // RenderTargetIdentifier, so resolve once to concrete identifiers to
                // avoid ambiguous (CS0121) cmd.Blit/SetRenderTarget overload resolution.
                RenderTargetIdentifier lightTarget = d.lightRT;
                RenderTargetIdentifier colorTarget = d.camColor;

                cmd.SetViewMatrix(d.view);
                cmd.SetProjectionMatrix(d.proj);

                var lcam = d.cam;
                float orthoH  = lcam.orthographicSize * 2f;
                float orthoW  = orthoH * lcam.aspect;
                float camMinX = lcam.transform.position.x - orthoW * 0.5f;
                float camMinY = lcam.transform.position.y - orthoH * 0.5f;
                Color deepAmbient = d.deepAmbientColor;
                bool isWorldCam = d.isWorldCam;

                // ── 1. Per-camera globals (read by LightSun/AmbientFill/Composite/Circle) ──
                cmd.SetGlobalVector("_CamWorldBounds", new Vector4(camMinX, camMinY, orthoW, orthoH));
                cmd.SetGlobalVector("_WorldToUV",      new Vector2(1f / orthoW, 1f / orthoH));
                cmd.SetGlobalFloat("_AmbientNormal",   d.ambientNormal);
                cmd.SetGlobalColor("_DeepAmbient",     deepAmbient);
                cmd.SetGlobalFloat("_SortRampRange",         d.sortRampRange);
                cmd.SetGlobalFloat("_BehindFarHeightFactor", d.behindFarHeightFactor);
                // Wall ray-march toggle for the point-light circle draw below. _WorldToUV
                // (set above) gives the march its world→screen-UV step.
                cmd.SetGlobalFloat("_PointShadows", d.pointShadows);
                // Flood-fill mode switch for the point-light circle draw below.
                cmd.SetGlobalFloat("_FloodFill", d.floodFill);
                // Bypass sky-exposure on non-world cameras — their _CamWorldBounds maps
                // screen UV to world positions that don't match what they draw.
                cmd.SetGlobalFloat("_SkyExposureBypass", isWorldCam ? 0f : 1f);

                // ── 2. Ambient setup ─────────────────────────────────────────────────
                cmd.SetRenderTarget(lightTarget);
                if (isWorldCam) {
                    // Deep ambient floor + spatial sky-light fill via _SkyExposureTex.
                    Color skyLight = SunController.GetAmbientColor();
                    cmd.ClearRenderTarget(false, true, deepAmbient);
                    cmd.SetGlobalColor("_AmbientColor", skyLight);
                    cmd.Blit(null, lightTarget, d.ambientFillMat);
                } else {
                    // Uniform clear to the time-of-day ambient color (no spatial modulation).
                    cmd.ClearRenderTarget(false, true, SunController.GetAmbientColor());
                }

                // ── 3. Point lights (world cam only; AABB-culled per light) ──────────
                if (isWorldCam) {
                    float camMaxX = camMinX + orthoW;
                    float camMaxY = camMinY + orthoH;
                    foreach (var src in LightSource.all) {
                        if (src == null || src.isDirectional || src.suppressed) continue;
                        Vector3 lp = src.transform.position;
                        float r = src.outerRadius;
                        if (lp.x + r < camMinX || lp.x - r > camMaxX
                         || lp.y + r < camMinY || lp.y - r > camMaxY) continue;

                        d.mpb.SetColor("_LightColor",    src.lightColor);
                        d.mpb.SetFloat("_Intensity",     src.intensity);
                        d.mpb.SetFloat("_InnerFraction",
                            src.outerRadius > 0f ? src.innerRadius / src.outerRadius : 0f);
                        d.mpb.SetVector("_LightWorldPos", (Vector4)lp);
                        d.mpb.SetFloat("_LightHeight",    src.lightHeight);
                        d.mpb.SetFloat("_LightSortBucket", src.sortBucket);
                        d.mpb.SetFloat("_CenterFlatten",   src.centerFlatten);

                        // Flood-fill mode: bind this light's geodesic reach field. A zero rect (no
                        // bake yet) makes the shader fall back to radial falloff for this light. The
                        // MPB persists between draws, so always set both (else a prior light's reach
                        // texture/rect would leak into this draw).
                        if (d.floodFill > 0.5f && src.reachTex != null) {
                            d.mpb.SetTexture("_ReachTex", src.reachTex);
                            d.mpb.SetVector("_ReachRect", src.reachRect);
                        } else {
                            d.mpb.SetTexture("_ReachTex", Texture2D.blackTexture);
                            d.mpb.SetVector("_ReachRect", Vector4.zero);
                        }

                        float diam = r * 2f;
                        var matrix = Matrix4x4.TRS(
                            new Vector3(lp.x, lp.y, 0f), Quaternion.identity, new Vector3(diam, diam, 1f));
                        cmd.DrawMesh(d.circleMesh, matrix, d.circleMat, 0, 0, d.mpb);
                    }
                }

                // ── 4. Directional lights (sun) ──────────────────────────────────────
                // Blit (not DrawMesh) — DrawMesh silently fails to write the temp RT for
                // some cameras (e.g. SkyCamera without PixelPerfectCamera).
                Color sunSkyContrib = Color.black;
                foreach (var src in LightSource.all) {
                    if (src == null || !src.isDirectional || src.intensity <= 0f) continue;
                    Vector3 sunDir = SunController.GetSunDirection();
                    cmd.SetGlobalColor("_SunColor",     src.lightColor);
                    cmd.SetGlobalFloat("_SunIntensity", src.intensity);
                    cmd.SetGlobalVector("_SunDir",      sunDir);
                    cmd.SetGlobalFloat("_SunHeight",    src.lightHeight);
                    cmd.Blit(null, lightTarget, d.sunMat);

                    // Replicate the sun shader's flat-normal NdotL for sky pixels.
                    Vector3 sunDir3 = new Vector3(sunDir.x, sunDir.y, -src.lightHeight).normalized;
                    float ndotlFlat = Mathf.Max(d.ambientNormal, -sunDir3.z);
                    sunSkyContrib += src.lightColor * (src.intensity * ndotlFlat);
                }

                // ── 5. Emission contribution to lightmap (world cam only) ────────────
                // Iterates the explicit emitter registry — only ~N draws/frame, vs one
                // shader invocation per visible lit sprite.
                if (isWorldCam && d.emissionMat != null && d.emissionStrength > 0f) {
                    cmd.SetGlobalFloat("_EmissionStrength", d.emissionStrength);
                    cmd.SetRenderTarget(lightTarget);
                    foreach (var src in LightSource.emitters) {
                        if (src == null) continue;
                        var rr = src.EmissionReceiver;
                        if (rr == null || !rr.enabled || !rr.gameObject.activeInHierarchy) continue;
                        cmd.SetGlobalFloat("_EmissionScale", src.CurrentEmissionScale);
                        cmd.SetGlobalFloat("_SortBucket",
                            SortBucketUtil.BucketToNormalized(SortBucketUtil.GetBucket(rr.sortingOrder)));
                        cmd.DrawRenderer(rr, d.emissionMat, 0, 0);
                    }
                }

                // ── 6. Composite — multiply-blit light RT onto the scene ─────────────
                // Sky pixels (normals alpha < 0.25) bypass the lightmap and use this
                // precomputed color: time-of-day ambient + sun (no exposure, no points).
                Color skyLightColor = SunController.GetAmbientColor() + sunSkyContrib;
                skyLightColor.r = Mathf.Clamp01(skyLightColor.r);
                skyLightColor.g = Mathf.Clamp01(skyLightColor.g);
                skyLightColor.b = Mathf.Clamp01(skyLightColor.b);
                skyLightColor.a = 1f;
                d.compositeMat.SetColor("_SkyLightColor", skyLightColor);
                d.compositeMat.SetFloat("_SkyLightBlend", d.skyLightBlend);
                cmd.Blit(lightTarget, colorTarget, d.compositeMat);

                cmd.EndSample(s_gpuSampler);
            });
        }
    }

    // Axis-aligned regular octagon, apothem 0.5, fan-triangulated from center.
    // Replaces the unit quad for point-light draws. The LightCircle shader's
    // radial smoothstep discards anything outside r=0.5 in UV space, so the
    // quad's four corners (~21% of fragment work per light) were always
    // wasted. The octagon's apothem matches r=0.5 — every covered pixel
    // contributes — saving ~17% of fragment invocations per point light.
    // See plans/gpu-perf-pass.md §Phase 7.
    static Mesh CreateOctagon() {
        const int   N = 8;
        const float R = 0.5f / 0.9238795325f;  // apothem / cos(π/8) = circumradius
        var verts = new Vector3[N + 1];
        var uvs   = new Vector2[N + 1];
        verts[0]  = Vector3.zero;
        uvs[0]    = new Vector2(0.5f, 0.5f);
        // Angles offset by π/8 so flats face cardinal axes (top/bottom/left/right).
        for (int i = 0; i < N; i++) {
            float a = Mathf.PI / 8f + i * (Mathf.PI / 4f);
            float x = R * Mathf.Cos(a);
            float y = R * Mathf.Sin(a);
            verts[i + 1] = new Vector3(x, y, 0f);
            // UV mirrors object position + 0.5 so the shader's `IN.uv - 0.5`
            // recovers the local position (identical to the old quad mapping).
            uvs[i + 1]   = new Vector2(x + 0.5f, y + 0.5f);
        }
        var tris = new int[N * 3];
        for (int i = 0; i < N; i++) {
            tris[i * 3 + 0] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = ((i + 1) % N) + 1;
        }
        var m = new Mesh { name = "LightOctagon" };
        m.SetVertices(verts);
        m.SetUVs(0, uvs);
        m.SetTriangles(tris, 0);
        m.UploadMeshData(true);
        return m;
    }
}
