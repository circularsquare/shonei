using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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
public class LightFeature : ScriptableRendererFeature {
    [Tooltip("Minimum NdotL floor applied to all lights — prevents back-faces from going fully black.")]
    [Range(0f, 1f)] public float ambientNormal = 0.15f; // ! set this in inspector

    // Shadow casting disabled for performance. Uncomment to re-enable:
    // [Header("Sun Shadows")]
    // [Range(0f, 5f)] public float shadowLength = 0.5f;
    // [Range(0f, 1f)] public float shadowDarkness = 0.6f;

    [Header("Underground")]
    [Tooltip("How far light penetrates into solid material, in tiles. Controls both sub-tile edge darkening and sky-light falloff.")]
    [Range(0.5f, 4f)] public float lightPenetrationDepth = 1f;
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
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
        if (!Application.isPlaying) return;
        // User-toggled lighting off → skip both passes. Sprites then render at
        // their raw URP color (no normals capture, no shadow modulation, no
        // composite multiply). Tolerate missing SettingsManager (defaults to on).
        if (SettingsManager.instance != null && !SettingsManager.instance.lightingEnabled) return;
        // Skip cameras that see no sprites participating in the normals RT pipeline
        // (lit, directional-only, or water). The Unlit overlay camera hits this check
        // because its culling mask only contains layers excluded from all three masks.
        var cullingMask = renderingData.cameraData.camera.cullingMask;
        if (cullingMask == 0) return;
        if ((cullingMask & (litLayers | directionalOnlyLayers | waterLayer | backgroundLayer | tileChunkLayer)) == 0) return;
        capturePass.Setup(litLayers, shadowCasterLayers, directionalOnlyLayers, waterLayer, backgroundLayer, tileChunkLayer);
        renderer.EnqueuePass(capturePass);
        lightPass.Setup(renderer.cameraColorTarget, ambientNormal, deepAmbientColor, skyLightBlend, sortRampRange, behindFarHeightFactor, emissionStrength, tileChunkLayer);
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
    // MarkerName is the CPU-side dispatch marker (ProfilerMarker.Auto in
    // Execute). s_gpuSampler is a CustomSampler created with collectGpuData=true
    // — this is REQUIRED for GPU timing to actually be captured. The plain
    // cmd.BeginSample(string) overload does NOT request GPU data; only the
    // CustomSampler overload does. Read via
    // Sampler.Get(name).GetRecorder().gpuElapsedNanoseconds.
    public const string MarkerName    = "Shonei.NormalsCapturePass";
    public const string GpuSampleName = "Shonei.NormalsCapturePass.GPU";
    static readonly ProfilerMarker s_marker    = new(MarkerName);
    static readonly CustomSampler  s_gpuSampler = CustomSampler.Create(GpuSampleName, true);

    static readonly int CapturedNormalsId = Shader.PropertyToID("_CapturedNormalsRT");
    // Global shader property — set per bucket iteration in Execute so the
    // override-material shaders (NormalsCapture, NormalsCaptureWater) write the
    // right sort bucket into the RT's B channel. Replaces the per-sprite _SortBucket
    // MPB. (Both chunked overrides instead read _SortBucket from a per-chunk MPB.)
    static readonly int SortBucketGlobalId = Shader.PropertyToID("_SortBucket");
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

    public void Setup(int litMask, int shadowMask, int dirOnlyMask, int waterMask, int backgroundMask, int tileChunkMask) {
        this.litMask          = litMask;
        this.shadowMask       = shadowMask;
        this.dirOnlyMask      = dirOnlyMask;
        this.waterMask        = waterMask;
        this.backgroundMask   = backgroundMask;
        this.tileChunkMask    = tileChunkMask;
    }

    public void Dispose() {
        CoreUtils.Destroy(mat);
        CoreUtils.Destroy(waterMat);
        CoreUtils.Destroy(chunkedMat);
        CoreUtils.Destroy(bgChunkedMat);
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd) {
        var desc = rd.cameraData.cameraTargetDescriptor;
        desc.depthBufferBits = 0;
        // Must use a format with an alpha channel — the lighting tier system encodes
        // shadow/lit/dirOnly information in alpha (1.0/0.5/0.3). The camera's default
        // HDR format (B10G11R11) has no alpha, so all tier checks silently return 1.0.
        desc.colorFormat = RenderTextureFormat.ARGB32;
        cmd.GetTemporaryRT(CapturedNormalsId, desc, FilterMode.Point);
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData rd) {
        if (mat == null) return;
        using var _ = s_marker.Auto();
        var cmd = CommandBufferPool.Get("NormalsCapture");
        // GPU-side sample bracket. BeginSample is recorded into the GPU
        // command stream; EndSample fires after all subsequent DrawRenderers
        // calls (they're submitted into the same stream by the SRP context),
        // so the captured GPU time covers clear + all draws.
        // MUST use the CustomSampler overload — the string overload does
        // not request GPU timing.
        cmd.BeginSample(s_gpuSampler);
        cmd.SetRenderTarget(CapturedNormalsId);

        // Skip per-sprite normal capture on non-world cameras (SkyCamera and
        // future overlay/minimap cams). They only need directional sun
        // lighting; with the right cleared values, LightSun and LightComposite
        // produce the same result as the previous per-sprite-with-flat-normal-
        // map path, but we skip the entire DrawRenderers workload.
        //
        // Clear color encodes flat camera-facing normal + directional-only
        // tier:
        //   rg = 0.5  → decoded normal xy = 0 → reconstructed = (0, 0, -1)
        //                 (camera-facing) — matches what cloud/hill normal
        //                 maps were producing anyway.
        //   b  = 0    → bucket 0 (unused; non-world cams skip point lights).
        //   a  = 0.3  → directional-only tier. Critical: LightComposite uses
        //                 alpha > 0.25 to decide "multiply by lightmap" vs
        //                 "use bright daytime _SkyLightColor fallback".
        //                 Without this, the sky would not darken at night.
        bool isWorldCam = (rd.cameraData.camera.cullingMask & tileChunkMask) != 0;
        Color clearColor = isWorldCam ? Color.clear : new Color(0.5f, 0.5f, 0f, 0.3f);
        cmd.ClearRenderTarget(false, true, clearColor);
        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        if (!isWorldCam) {
            cmd.EndSample(s_gpuSampler);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            return;
        }

        // ── Per-bucket sprite draws ─────────────────────────────────────────
        // Preserve the original tier order: background → dirOnly → litOnly →
        // tileChunk → shadow → water. Later draws overwrite earlier ones in
        // the normals RT; tiles must overwrite background (so a tile pixel
        // gets shadow-caster alpha + tile normals, not lit-only alpha + flat
        // normals), and shadow casters must overwrite tiles (so an animal
        // standing on a tile shows its own normals/bucket).
        //
        // Implementation: two bucket loops with the chunked tile draw between
        // them. The first loop runs background/dirOnly/litOnly per bucket;
        // the second runs shadow/water per bucket. Chunked tiles draw once
        // outside any loop (they carry _SortBucket per-chunk via MPB — the
        // bucket loop's global _SortBucket wouldn't reach them anyway).
        //
        // Each bucket sets _SortBucket as a global; the override shaders
        // (NormalsCapture / NormalsCaptureWater) read it from the global, no
        // per-sprite MPB needed.
        // Both chunk layers are drawn by their own dedicated array-sampling overrides,
        // never the generic sprite override (which samples _MainTex, not _MainTexArr) —
        // exclude them from the generic lit/shadow tiers so they aren't double-drawn wrong.
        // Background walls reuse the Background layer (chunked meshes replaced the old mask
        // sprite), so exclude backgroundMask here and draw it with bgChunkedMat below.
        int litOnlyMask = litMask & ~shadowMask & ~dirOnlyMask & ~backgroundMask;
        int shadowOnlyMask = litMask & shadowMask & ~dirOnlyMask & ~tileChunkMask & ~backgroundMask;

        // ── Chunked background walls (single draw, BEFORE every other tier) ──
        // Background is the backmost tier — drawn first so dirOnly/litOnly/tile/shadow/
        // water all overwrite it where they overlap (the same slot the old background
        // sprite held: clouds etc. must win over it). The chunked meshes live on the
        // Background layer and carry their own _SortBucket per chunk via MPB, so this
        // draws once outside the bucket loops, with the flat lit-only (alpha 0.5) override.
        if (backgroundMask != 0 && bgChunkedMat != null) {
            var ds = CreateDrawingSettings(
                new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
            ds.overrideMaterial          = bgChunkedMat;
            ds.overrideMaterialPassIndex = 0; // single pass
            var fs = new FilteringSettings(RenderQueueRange.all, backgroundMask);
            context.DrawRenderers(rd.cullResults, ref ds, ref fs);
        }

        // ── Loop A: tiers that draw BEFORE chunked tiles ────────────────────
        for (int b = 0; b < SortBucketUtil.BucketCount; b++) {
            cmd.SetGlobalFloat(SortBucketGlobalId, SortBucketUtil.BucketToNormalized(b));
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            uint bucketMask = 1u << b;

            // (Background walls are drawn once above the loop with bgChunkedMat — the
            // old per-bucket NormalsCaptureBackground sprite draw is retired.)

            // Directional-only (pass 2, alpha=0.3).
            if (dirOnlyMask != 0) {
                var ds = CreateDrawingSettings(
                    new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
                ds.overrideMaterial          = mat;
                ds.overrideMaterialPassIndex = 2;
                var fs = new FilteringSettings(RenderQueueRange.all, dirOnlyMask);
                fs.renderingLayerMask = bucketMask;
                context.DrawRenderers(rd.cullResults, ref ds, ref fs);
            }

            // Lit-only (pass 1, alpha=0.5). dirOnly excluded — drawn above.
            if (litOnlyMask != 0) {
                var ds = CreateDrawingSettings(
                    new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
                ds.overrideMaterial          = mat;
                ds.overrideMaterialPassIndex = 1;
                var fs = new FilteringSettings(RenderQueueRange.all, litOnlyMask);
                fs.renderingLayerMask = bucketMask;
                context.DrawRenderers(rd.cullResults, ref ds, ref fs);
            }
        }

        // ── Chunked tile meshes (single draw, between the two bucket loops) ─
        // Chunked tiles carry their own _SortBucket via per-chunk MPB
        // (TileMeshController) — necessary because each chunk binds its own
        // Texture2DArray slices and that MPB is unavoidable. Position-wise
        // they sit AFTER background/litOnly (so their shadow-caster alpha
        // overwrites the lit-only alpha at overlap pixels — otherwise
        // underground tiles with background behind them would lose their
        // edge-depth darkening) and BEFORE shadow casters (so animals
        // standing on tiles win the overlap).
        if (tileChunkMask != 0 && chunkedMat != null) {
            var ds = CreateDrawingSettings(
                new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
            ds.overrideMaterial          = chunkedMat;
            ds.overrideMaterialPassIndex = 0; // chunked shader has a single pass — shadow caster
            var fs = new FilteringSettings(RenderQueueRange.all, tileChunkMask);
            context.DrawRenderers(rd.cullResults, ref ds, ref fs);
        }

        // ── Loop B: tiers that draw AFTER chunked tiles ─────────────────────
        for (int b = 0; b < SortBucketUtil.BucketCount; b++) {
            cmd.SetGlobalFloat(SortBucketGlobalId, SortBucketUtil.BucketToNormalized(b));
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            uint bucketMask = 1u << b;

            // Shadow casters (pass 0, alpha=1.0). tileChunk excluded — drawn
            // between the loops. Animals/structures overwrite tile pixels
            // where they overlap.
            {
                var ds = CreateDrawingSettings(
                    new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
                ds.overrideMaterial          = mat;
                ds.overrideMaterialPassIndex = 0;
                var fs = new FilteringSettings(RenderQueueRange.all, shadowOnlyMask);
                fs.renderingLayerMask = bucketMask;
                context.DrawRenderers(rd.cullResults, ref ds, ref fs);
            }

            // Water (pass 1, alpha=0.5).
            if (waterMask != 0 && waterMat != null) {
                var ds = CreateDrawingSettings(
                    new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
                ds.overrideMaterial          = waterMat;
                ds.overrideMaterialPassIndex = 1;
                var fs = new FilteringSettings(RenderQueueRange.all, waterMask);
                fs.renderingLayerMask = bucketMask;
                context.DrawRenderers(rd.cullResults, ref ds, ref fs);
            }
        }

        cmd.EndSample(s_gpuSampler);
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public override void OnCameraCleanup(CommandBuffer cmd) {
        cmd.ReleaseTemporaryRT(CapturedNormalsId);
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

    static readonly int LightRTId = Shader.PropertyToID("_CustomLightRT");
    float ambientNormal;
    float skyLightBlend;
    float sortRampRange;
    float behindFarHeightFactor;
    float emissionStrength = 1f;
    int   tileChunkMask;
    Color deepAmbientColor;
    RenderTargetIdentifier colorBuffer;
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

    public void Setup(RenderTargetIdentifier colorBuffer, float ambientNormal, Color deepAmbientColor, float skyLightBlend, float sortRampRange, float behindFarHeightFactor, float emissionStrength, int tileChunkMask) {
        this.colorBuffer           = colorBuffer;
        this.ambientNormal         = ambientNormal;
        this.deepAmbientColor      = deepAmbientColor;
        this.skyLightBlend         = skyLightBlend;
        this.sortRampRange         = sortRampRange;
        this.behindFarHeightFactor = behindFarHeightFactor;
        this.emissionStrength      = emissionStrength;
        this.tileChunkMask         = tileChunkMask;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd) {
        var desc = rd.cameraData.cameraTargetDescriptor;
        desc.depthBufferBits = 0;
        cmd.GetTemporaryRT(LightRTId, desc, FilterMode.Bilinear);
        // Required: binds the RT so ClearRenderTarget in Execute targets it.
        ConfigureTarget(LightRTId);
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData rd) {
        if (circleMat == null || sunMat == null || compositeMat == null) return;
        using var _ = s_marker.Auto();
        var cmd = CommandBufferPool.Get("CustomLighting");
        cmd.BeginSample(s_gpuSampler);

        var view = rd.cameraData.GetViewMatrix();
        var proj = GL.GetGPUProjectionMatrix(rd.cameraData.GetProjectionMatrix(), renderIntoTexture: false);
        cmd.SetViewMatrix(view);
        cmd.SetProjectionMatrix(proj);

        // ── 1. Per-camera globals ────────────────────────────────────────────
        // These are read by multiple shaders (LightSun, LightAmbientFill,
        // LightComposite, LightCircle) and must be set for EVERY camera
        // before any drawing. Keeping them in one block prevents the bug
        // where the sky camera silently inherits stale Main Camera values.
        var cam = rd.cameraData.camera;
        float orthoH  = cam.orthographicSize * 2f;
        float orthoW  = orthoH * cam.aspect;
        float camMinX = cam.transform.position.x - orthoW * 0.5f;
        float camMinY = cam.transform.position.y - orthoH * 0.5f;

        Color deepAmbient = deepAmbientColor;

        // ── World-aligned vs sky-style lighting ─────────────────────────────
        // A "world camera" renders the chunked tile layer — by definition the
        // only camera whose frustum aligns with the world tile grid that built
        // _SkyExposureTex. Everything else (SkyCamera, future overlay/parallax
        // cameras, minimap previews, …) gets uniform sky-style lighting: no
        // spatial exposure, no torches, no emission. This is a fail-safe
        // default — a new camera "just works" without inheriting Main-camera
        // assumptions and ghosting the terrain onto its render. Opt in by
        // adding the TileChunk layer to the camera's culling mask.
        bool isWorldCam = (cam.cullingMask & tileChunkMask) != 0;

        cmd.SetGlobalVector("_CamWorldBounds", new Vector4(camMinX, camMinY, orthoW, orthoH));
        cmd.SetGlobalVector("_WorldToUV",      new Vector2(1f / orthoW, 1f / orthoH));
        cmd.SetGlobalFloat("_AmbientNormal",   ambientNormal);
        cmd.SetGlobalColor("_DeepAmbient",     deepAmbient);
        // Ramp params consumed by LightCircle.shader for sort-aware effective height.
        cmd.SetGlobalFloat("_SortRampRange",         sortRampRange);
        cmd.SetGlobalFloat("_BehindFarHeightFactor", behindFarHeightFactor);
        // Read by LightSun.shader. Bypass on non-world cameras because their
        // _CamWorldBounds maps screen UV to world positions that don't
        // correspond to what they actually draw — sampling exposure would
        // ghost the world tile-grid silhouette onto sky/background content.
        cmd.SetGlobalFloat("_SkyExposureBypass", isWorldCam ? 0f : 1f);

        // ── 2. Ambient setup (camera-specific) ──────────────────────────────
        // World cam: deep ambient floor + spatial sky-light fill via _SkyExposureTex
        //   → underground stays dark, surface gets full ambient.
        // Non-world cam: uniform clear to the time-of-day ambient color — no
        //   spatial modulation, since the camera's content (clouds, sky,
        //   parallax background) is not anchored to the world tile grid.

        if (isWorldCam) {
            Color skyLight = SunController.GetAmbientColor();
            cmd.ClearRenderTarget(false, true, deepAmbient);
            cmd.SetGlobalColor("_AmbientColor", skyLight);
            cmd.Blit(null, LightRTId, ambientFillMat);
        } else {
            cmd.ClearRenderTarget(false, true, SunController.GetAmbientColor());
        }

        // ── 3. Point lights (torches, lanterns, etc.) ────────────────────────
        // World cam only — torches/lanterns live on world tile positions, so
        // non-world cameras (which draw parallax/sky content) ignore them.
        // Lights are culled per-light against the camera AABB so off-screen
        // torches don't issue draw calls. The cull uses outerRadius as the
        // buffer — a light just off-screen still illuminates on-screen pixels
        // as long as its reach extends into view.
        if (isWorldCam) {
            float camMaxX = camMinX + orthoW;
            float camMaxY = camMinY + orthoH;
            foreach (var src in LightSource.all) {
                if (src == null || src.isDirectional) continue;
                Vector3 lp = src.transform.position;
                float r = src.outerRadius;
                if (lp.x + r < camMinX || lp.x - r > camMaxX
                 || lp.y + r < camMinY || lp.y - r > camMaxY) continue;

                mpb.SetColor("_LightColor",    src.lightColor);
                mpb.SetFloat("_Intensity",     src.intensity);
                mpb.SetFloat("_InnerFraction",
                    src.outerRadius > 0f ? src.innerRadius / src.outerRadius : 0f);
                mpb.SetVector("_LightWorldPos", (Vector4)lp);
                mpb.SetFloat("_LightHeight",    src.lightHeight);
                mpb.SetFloat("_LightSortBucket", src.sortBucket);
                mpb.SetFloat("_CenterFlatten",   src.centerFlatten);

                float d = r * 2f;
                var matrix = Matrix4x4.TRS(
                    new Vector3(lp.x, lp.y, 0f),
                    Quaternion.identity,
                    new Vector3(d, d, 1f));

                cmd.DrawMesh(circleMesh, matrix, circleMat, 0, 0, mpb);
            }
        }

        // ── 4. Directional lights (sun) — NdotL × shadow march ──────────────
        // Accumulate flat-normal sun contribution for the sky-pixel uniform.
        // Sky pixels use a camera-facing normal (0,0,-1), so NdotL = sunHeight/mag.
        Color sunSkyContrib = Color.black;
        foreach (var src in LightSource.all) {
            if (src == null || !src.isDirectional || src.intensity <= 0f) continue;
            Vector3 sunDir = SunController.GetSunDirection();
            cmd.SetGlobalColor("_SunColor",     src.lightColor);
            cmd.SetGlobalFloat("_SunIntensity", src.intensity);
            cmd.SetGlobalVector("_SunDir",      sunDir);
            cmd.SetGlobalFloat("_SunHeight",    src.lightHeight);
            // Blit instead of DrawMesh — DrawMesh silently fails to write to
            // the temp RT for some cameras (e.g. SkyCamera without PixelPerfectCamera).
            cmd.Blit(null, LightRTId, sunMat);

            // Replicate sun shader's flat-normal NdotL for sky pixels.
            Vector3 sunDir3 = new Vector3(sunDir.x, sunDir.y, -src.lightHeight).normalized;
            float ndotlFlat = Mathf.Max(ambientNormal, -sunDir3.z); // dot((0,0,-1), sunDir3)
            sunSkyContrib += src.lightColor * (src.intensity * ndotlFlat);
        }

        // ── 5. Emission contribution to lightmap ─────────────────────────────
        // Sprites with an `_EmissionMap` secondary texture (wired by
        // SpriteNormalMapGenerator from a `{stem}_f.png` companion) write
        // white-by-mask additively into the lightmap. After composite, those
        // pixels keep their painted color verbatim — torches/fireplaces glow
        // regardless of time-of-day or distance from a real LightSource.
        //
        // Iterates the explicit emitter registry (LightSource.emitters)
        // instead of DrawRenderers over the entire litMask — only ~N draws per
        // frame where N = active emitters, vs. one shader invocation per visible
        // lit sprite. World cam only — emitters live at world tile positions.
        //
        // `_EmissionScale` and `_SortBucket` are set as globals per-emitter
        // (replacing the older per-renderer MPB scheme). Globals don't break
        // SRP Batcher; the emission pass draws one emitter at a time so
        // there's no batching concern here anyway.
        if (isWorldCam && emissionMat != null && emissionStrength > 0f) {
            cmd.SetGlobalFloat("_EmissionStrength", emissionStrength);
            cmd.SetRenderTarget(LightRTId);
            foreach (var src in LightSource.emitters) {
                if (src == null) continue;
                var r = src.EmissionReceiver;
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                cmd.SetGlobalFloat("_EmissionScale", src.CurrentEmissionScale);
                cmd.SetGlobalFloat("_SortBucket",
                    SortBucketUtil.BucketToNormalized(SortBucketUtil.GetBucket(r.sortingOrder)));
                cmd.DrawRenderer(r, emissionMat, 0, 0);
            }
        }

        // ── 6. Composite — multiply-blit light RT onto the scene ─────────────
        // Sky pixels bypass the lightmap entirely and use this precomputed color:
        // time-of-day ambient + sun (no sky-exposure modulation, no point lights).
        Color skyLightColor = SunController.GetAmbientColor() + sunSkyContrib;
        skyLightColor.r = Mathf.Clamp01(skyLightColor.r);
        skyLightColor.g = Mathf.Clamp01(skyLightColor.g);
        skyLightColor.b = Mathf.Clamp01(skyLightColor.b);
        skyLightColor.a = 1f;
        compositeMat.SetColor("_SkyLightColor", skyLightColor);
        compositeMat.SetFloat("_SkyLightBlend", skyLightBlend);
        cmd.Blit(LightRTId, colorBuffer, compositeMat);

        cmd.EndSample(s_gpuSampler);
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public override void OnCameraCleanup(CommandBuffer cmd) {
        cmd.ReleaseTemporaryRT(LightRTId);
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
