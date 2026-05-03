using System.Collections.Generic;
using UnityEngine;
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
    [Tooltip("Sort-bucket delta (in normalized units, 1.0 = 255 sortingOrder units) over " +
             "which the effective light height ramps across BEHIND receivers from +lightHeight " +
             "(at delta=0) toward +lightHeight * behindFarHeightFactor (at full range). " +
             "0.08 ≈ 20 sortingOrder units. In-front receivers always use -lightHeight " +
             "(hard flip), independent of this range.")]
    [Range(0.01f, 0.5f)] public float sortRampRange = 0.08f;
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
    [Tooltip("Background layer — rendered into the normals RT using NormalsCaptureBackground, " +
             "which clips transparent top pixels so they read as sky. Lit-only (no shadow cast).")]
    public LayerMask backgroundLayer = 0; // set to the 'Background' layer in the inspector

    NormalsCapturePass capturePass;
    LightPass          lightPass;

    public override void Create() {
        penetrationDepth = lightPenetrationDepth;
        capturePass = new NormalsCapturePass {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
        };
        lightPass = new LightPass {
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
        if ((cullingMask & (litLayers | directionalOnlyLayers | waterLayer | backgroundLayer)) == 0) return;
        capturePass.Setup(litLayers, shadowCasterLayers, directionalOnlyLayers, waterLayer, backgroundLayer);
        renderer.EnqueuePass(capturePass);
        lightPass.Setup(renderer.cameraColorTarget, ambientNormal, deepAmbientColor, skyLightBlend, sortRampRange, behindFarHeightFactor, emissionStrength);
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
    static readonly int CapturedNormalsId = Shader.PropertyToID("_CapturedNormalsRT");
    readonly Material mat;
    readonly Material waterMat;
    readonly Material backgroundMat;
    int litMask     = ~0;
    int shadowMask  = ~0;
    int dirOnlyMask = 0;
    int waterMask   = 0;
    int backgroundMask  = 0;

    public NormalsCapturePass() {
        mat      = CoreUtils.CreateEngineMaterial("Hidden/NormalsCapture");
        waterMat  = CoreUtils.CreateEngineMaterial("Hidden/NormalsCaptureWater");
        backgroundMat = CoreUtils.CreateEngineMaterial("Hidden/NormalsCaptureBackground");
    }

    public void Setup(int litMask, int shadowMask, int dirOnlyMask, int waterMask, int backgroundMask) {
        this.litMask     = litMask;
        this.shadowMask  = shadowMask;
        this.dirOnlyMask = dirOnlyMask;
        this.waterMask   = waterMask;
        this.backgroundMask  = backgroundMask;
    }

    public void Dispose() {
        CoreUtils.Destroy(mat);
        CoreUtils.Destroy(waterMat);
        CoreUtils.Destroy(backgroundMat);
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
        var cmd = CommandBufferPool.Get("NormalsCapture");
        cmd.SetRenderTarget(CapturedNormalsId);
        cmd.ClearRenderTarget(false, true, Color.clear); // black = flat fallback in light shaders
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        // Draw background first (pass 1, lit-only) — uses NormalsCaptureBackground which
        // clips transparent top pixels so they read as sky. Drawn earliest so any
        // tile or sprite that overlaps the background overwrites it in the normals RT.
        if (backgroundMask != 0 && backgroundMat != null) {
            var ds = CreateDrawingSettings(
                new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
            ds.overrideMaterial          = backgroundMat;
            ds.overrideMaterialPassIndex = 1; // alpha = 0.5 (lit-only)

            var fs = new FilteringSettings(RenderQueueRange.all, backgroundMask);
            context.DrawRenderers(rd.cullResults, ref ds, ref fs);
        }

        // Draw directional-only sprites (pass 2, alpha=0.3) — sun + ambient only, no torch light.
        // Drawn first; litOnlyMask and shadowMask both exclude dirOnlyMask so they can't overwrite.
        if (dirOnlyMask != 0) {
            var ds = CreateDrawingSettings(
                new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
            ds.overrideMaterial          = mat;
            ds.overrideMaterialPassIndex = 2; // alpha = 0.3 (directional-only)

            var fs = new FilteringSettings(RenderQueueRange.all, dirOnlyMask);
            context.DrawRenderers(rd.cullResults, ref ds, ref fs);
        }

        // Draw lit-only sprites next (pass 1, alpha=0.5) — no shadow cast.
        // Shadow casters are drawn last (pass 0, alpha=1.0) so they overwrite on overlap.
        // dirOnlyMask is excluded from both: those sprites were already drawn above with pass 2.
        int litOnlyMask = litMask & ~shadowMask & ~dirOnlyMask;
        if (litOnlyMask != 0) {
            var ds = CreateDrawingSettings(
                new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
            ds.overrideMaterial          = mat;
            ds.overrideMaterialPassIndex = 1; // alpha = 0.5 (lit, no shadow)

            var fs = new FilteringSettings(RenderQueueRange.all, litOnlyMask);
            context.DrawRenderers(rd.cullResults, ref ds, ref fs);
        }

        // Draw shadow-casting sprites (pass 0, alpha=1.0).
        {
            var ds = CreateDrawingSettings(
                new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
            ds.overrideMaterial          = mat;
            ds.overrideMaterialPassIndex = 0; // alpha = 1.0 (casts shadow)

            var fs = new FilteringSettings(RenderQueueRange.all, litMask & shadowMask & ~dirOnlyMask);
            context.DrawRenderers(rd.cullResults, ref ds, ref fs);
        }

        // Draw water as lit-only (pass 1, alpha=0.5): torch + sun light, no shadow cast.
        // Uses NormalsCaptureWater which samples the global _WaterSurfaceTex set by
        // WaterController, so only pixels with actual water get normals written.
        if (waterMask != 0 && waterMat != null) {
            var ds = CreateDrawingSettings(
                new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
            ds.overrideMaterial          = waterMat;
            ds.overrideMaterialPassIndex = 1; // alpha = 0.5 (lit-only)

            var fs = new FilteringSettings(RenderQueueRange.all, waterMask);
            context.DrawRenderers(rd.cullResults, ref ds, ref fs);
        }

    }

    public override void OnCameraCleanup(CommandBuffer cmd) {
        cmd.ReleaseTemporaryRT(CapturedNormalsId);
    }
}

// ── Light Pass ────────────────────────────────────────────────────────────────

class LightPass : ScriptableRenderPass, System.IDisposable {
    static readonly int LightRTId = Shader.PropertyToID("_CustomLightRT");

    float ambientNormal;
    float skyLightBlend;
    float sortRampRange;
    float behindFarHeightFactor;
    float emissionStrength = 1f;
    Color deepAmbientColor;
    RenderTargetIdentifier colorBuffer;
    readonly Material circleMat;
    readonly Material sunMat;
    readonly Material compositeMat;
    readonly Material ambientFillMat;
    readonly Material emissionMat;
    readonly Mesh     quad;
    readonly MaterialPropertyBlock mpb = new();

    // Cache sky camera check per camera to avoid GetComponent every frame.
    readonly Dictionary<Camera, bool> skyCamCache = new();

    public LightPass() {
        circleMat       = CoreUtils.CreateEngineMaterial("Hidden/LightCircle");
        sunMat          = CoreUtils.CreateEngineMaterial("Hidden/LightSun");
        compositeMat    = CoreUtils.CreateEngineMaterial("Hidden/LightComposite");
        ambientFillMat  = CoreUtils.CreateEngineMaterial("Hidden/LightAmbientFill");
        emissionMat     = CoreUtils.CreateEngineMaterial("Hidden/EmissionWriter");
        quad            = CreateQuad();
    }

    public void Dispose() {
        CoreUtils.Destroy(circleMat);
        CoreUtils.Destroy(sunMat);
        CoreUtils.Destroy(compositeMat);
        CoreUtils.Destroy(ambientFillMat);
        CoreUtils.Destroy(emissionMat);
        CoreUtils.Destroy(quad);
    }

    public void Setup(RenderTargetIdentifier colorBuffer, float ambientNormal, Color deepAmbientColor, float skyLightBlend, float sortRampRange, float behindFarHeightFactor, float emissionStrength) {
        this.colorBuffer           = colorBuffer;
        this.ambientNormal         = ambientNormal;
        this.deepAmbientColor      = deepAmbientColor;
        this.skyLightBlend         = skyLightBlend;
        this.sortRampRange         = sortRampRange;
        this.behindFarHeightFactor = behindFarHeightFactor;
        this.emissionStrength      = emissionStrength;
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

        var cmd = CommandBufferPool.Get("CustomLighting");

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

        cmd.SetGlobalVector("_CamWorldBounds", new Vector4(camMinX, camMinY, orthoW, orthoH));
        cmd.SetGlobalVector("_WorldToUV",      new Vector2(1f / orthoW, 1f / orthoH));
        cmd.SetGlobalFloat("_AmbientNormal",   ambientNormal);
        cmd.SetGlobalColor("_DeepAmbient",     deepAmbient);
        // Ramp params consumed by LightCircle.shader for sort-aware effective height.
        cmd.SetGlobalFloat("_SortRampRange",         sortRampRange);
        cmd.SetGlobalFloat("_BehindFarHeightFactor", behindFarHeightFactor);

        // ── 2. Ambient setup (camera-specific) ──────────────────────────────
        // Deep ambient: constant color, always present including deep underground.
        // Sky light: full ambient color (day/night cycle), modulated by _SkyExposureTex.
        // Sky camera gets full ambient (no spatial exposure) so clouds
        // aren't affected by the underground cutoff.

        if (!skyCamCache.TryGetValue(cam, out bool isSkyCam))
            skyCamCache[cam] = isSkyCam = cam.GetComponent<SkyCamera>() != null;
        if (isSkyCam) {
            cmd.ClearRenderTarget(false, true, SunController.GetAmbientColor());
        } else {
            Color skyLight = SunController.GetAmbientColor();
            cmd.ClearRenderTarget(false, true, deepAmbient);
            cmd.SetGlobalColor("_AmbientColor", skyLight);
            cmd.Blit(null, LightRTId, ambientFillMat);
        }

        // ── 3. Point lights (torches, lanterns, etc.) ────────────────────────
        // Skipped for SkyCamera (no torches in the sky), and culled per-light
        // against the camera AABB so off-screen torches don't issue draw calls.
        // The cull uses outerRadius as the buffer — a light just off-screen still
        // illuminates on-screen pixels as long as its reach extends into view.
        if (!isSkyCam) {
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

                float d = r * 2f;
                var matrix = Matrix4x4.TRS(
                    new Vector3(lp.x, lp.y, 0f),
                    Quaternion.identity,
                    new Vector3(d, d, 1f));

                cmd.DrawMesh(quad, matrix, circleMat, 0, 0, mpb);
            }
        }

        // ── 4. Directional lights (sun) — NdotL × shadow march ──────────────
        // Accumulate flat-normal sun contribution for the sky-pixel uniform.
        // Sky pixels use a camera-facing normal (0,0,-1), so NdotL = sunHeight/mag.
        Color sunSkyContrib = Color.black;
        foreach (var src in LightSource.all) {
            if (src == null || !src.isDirectional || src.intensity <= 0f) continue;
            cmd.SetGlobalColor("_SunColor",     src.lightColor);
            cmd.SetGlobalFloat("_SunIntensity", src.intensity);
            cmd.SetGlobalVector("_SunDir",      SunController.GetSunDirection());
            cmd.SetGlobalFloat("_SunHeight",    src.lightHeight);
            // Blit instead of DrawMesh — DrawMesh silently fails to write to
            // the temp RT for some cameras (e.g. SkyCamera without PixelPerfectCamera).
            cmd.Blit(null, LightRTId, sunMat);

            // Replicate sun shader's flat-normal NdotL for sky pixels.
            Vector3 sunDir2 = SunController.GetSunDirection();
            Vector3 sunDir3 = new Vector3(sunDir2.x, sunDir2.y, -src.lightHeight).normalized;
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
        // Iterates the explicit emissive registry (LightSource.emissiveReceivers)
        // instead of DrawRenderers over the entire litMask — only ~N draws per
        // frame where N = active emitters, vs. one shader invocation per visible
        // lit sprite. Skipped for SkyCamera (clouds carry no emission).
        if (!isSkyCam && emissionMat != null && emissionStrength > 0f) {
            cmd.SetGlobalFloat("_EmissionStrength", emissionStrength);
            cmd.SetRenderTarget(LightRTId);
            foreach (var r in LightSource.emissiveReceivers) {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
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

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    public override void OnCameraCleanup(CommandBuffer cmd) {
        cmd.ReleaseTemporaryRT(LightRTId);
    }

    static Mesh CreateQuad() {
        var m = new Mesh { name = "LightQuad" };
        m.SetVertices(new Vector3[] {
            new(-0.5f, -0.5f, 0), new(0.5f, -0.5f, 0),
            new(-0.5f,  0.5f, 0), new(0.5f,  0.5f, 0),
        });
        m.SetUVs(0, new Vector2[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one });
        m.SetTriangles(new int[] { 0, 2, 1, 2, 3, 1 }, 0);
        m.UploadMeshData(true);
        return m;
    }
}
