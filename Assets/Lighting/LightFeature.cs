using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// ScriptableRendererFeature — add this to your Universal Renderer Data asset.
// Unified lighting pass: all lights (ambient, sun, torches) use Max blend.
//
// Pipeline each frame:
//   1. NormalsCapturePass renders all sprites' _NormalMap textures into
//      _CapturedNormalsRT (world-space normals, packed 0–1).
//   2. Clear light RT to SunController.GetAmbientColor().
//   3. Draw point LightSources (torches) as circle quads — NdotL × radial falloff.
//   4. Draw directional LightSources (sun) fullscreen — NdotL from _CapturedNormalsRT.
//      Shadow: 16-step ray march in LightSun.shader along the sun direction.
//   5. Multiply-blit light RT onto scene: final = scene × lightmap.
public class LightFeature : ScriptableRendererFeature {
    [Tooltip("Minimum NdotL floor applied to all lights — prevents back-faces from going fully black.")]
    [Range(0f, 1f)] public float ambientNormal = 0.50f;

    [Header("Sun Shadows")]
    [Tooltip("Shadow length in world units. Automatically scales with camera zoom/PPU.")]
    [Range(0f, 5f)] public float shadowLength = 0.5f;
    [Tooltip("How dark the shadow is. 0 = no shadow, 1 = fully blocks sun.")]
    [Range(0f, 1f)] public float shadowDarkness = 0.6f;

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

    NormalsCapturePass capturePass;
    LightPass          lightPass;

    public override void Create() {
        capturePass = new NormalsCapturePass {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
        };
        lightPass = new LightPass {
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
        if (!Application.isPlaying) return;
        // Skip cameras that see no sprites participating in the normals RT pipeline
        // (lit, directional-only, or water). The Unlit overlay camera hits this check
        // because its culling mask only contains layers excluded from all three masks.
        var cullingMask = renderingData.cameraData.camera.cullingMask;
        if (cullingMask == 0) return;
        if ((cullingMask & (litLayers | directionalOnlyLayers | waterLayer)) == 0) return;
        capturePass.Setup(litLayers, shadowCasterLayers, directionalOnlyLayers, waterLayer);
        renderer.EnqueuePass(capturePass);
        lightPass.Setup(renderer.cameraColorTarget, ambientNormal, shadowLength, shadowDarkness);
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
    int litMask     = ~0;
    int shadowMask  = ~0;
    int dirOnlyMask = 0;
    int waterMask   = 0;

    public NormalsCapturePass() {
        mat      = CoreUtils.CreateEngineMaterial("Hidden/NormalsCapture");
        waterMat = CoreUtils.CreateEngineMaterial("Hidden/NormalsCaptureWater");
    }

    public void Setup(int litMask, int shadowMask, int dirOnlyMask, int waterMask) {
        this.litMask     = litMask;
        this.shadowMask  = shadowMask;
        this.dirOnlyMask = dirOnlyMask;
        this.waterMask   = waterMask;
    }

    public void Dispose() {
        CoreUtils.Destroy(mat);
        CoreUtils.Destroy(waterMat);
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
    float shadowLength;
    float shadowDarkness;
    RenderTargetIdentifier colorBuffer;
    readonly Material circleMat;
    readonly Material sunMat;
    readonly Material compositeMat;
    readonly Mesh     quad;

    public LightPass() {
        circleMat    = CoreUtils.CreateEngineMaterial("Hidden/LightCircle");
        sunMat       = CoreUtils.CreateEngineMaterial("Hidden/LightSun");
        compositeMat = CoreUtils.CreateEngineMaterial("Hidden/LightComposite");
        quad         = CreateQuad();
    }

    public void Dispose() {
        CoreUtils.Destroy(circleMat);
        CoreUtils.Destroy(sunMat);
        CoreUtils.Destroy(compositeMat);
        CoreUtils.Destroy(quad);
    }

    public void Setup(RenderTargetIdentifier colorBuffer, float ambientNormal, float shadowLength, float shadowDarkness) {
        this.colorBuffer    = colorBuffer;
        this.ambientNormal  = ambientNormal;
        this.shadowLength   = shadowLength;
        this.shadowDarkness = shadowDarkness;
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

        // ── 1. Clear light RT to ambient color ───────────────────────────────
        Color ambient = SunController.GetAmbientColor();
        cmd.ClearRenderTarget(false, true, ambient);

        // ── 2. Draw point lights (torches, lanterns, etc.) — Max blend ───────
        var mpb = new MaterialPropertyBlock();
        foreach (var src in LightSource.all) {
            if (src == null || src.isDirectional) continue;
            mpb.SetColor("_LightColor",    src.lightColor);
            mpb.SetFloat("_Intensity",     src.intensity);
            mpb.SetFloat("_InnerFraction",
                src.outerRadius > 0f ? src.innerRadius / src.outerRadius : 0f);
            mpb.SetVector("_LightWorldPos", (Vector4)src.transform.position);
            mpb.SetFloat("_LightHeight",   src.lightHeight);
            mpb.SetFloat("_AmbientNormal", ambientNormal);

            float d = src.outerRadius * 2f;
            var matrix = Matrix4x4.TRS(
                new Vector3(src.transform.position.x, src.transform.position.y, 0f),
                Quaternion.identity,
                new Vector3(d, d, 1f));

            cmd.DrawMesh(quad, matrix, circleMat, 0, 0, mpb);
        }

        // ── 3. Draw directional lights (sun) — NdotL × shadow march ──────────
        // World→UV scale lets LightSun.shader convert shadow length (world units) to UV offset.
        var cam = rd.cameraData.camera;
        float orthoH = cam.orthographicSize * 2f;
        float orthoW = orthoH * cam.aspect;
        cmd.SetGlobalVector("_WorldToUV",     new Vector2(1f / orthoW, 1f / orthoH));
        cmd.SetGlobalFloat("_ShadowLength",   shadowLength);
        cmd.SetGlobalFloat("_ShadowDarkness", shadowDarkness);

        foreach (var src in LightSource.all) {
            if (src == null || !src.isDirectional || src.intensity <= 0f) continue;
            cmd.SetGlobalColor("_SunColor",      src.lightColor);
            cmd.SetGlobalFloat("_SunIntensity",  src.intensity);
            cmd.SetGlobalVector("_SunDir",       SunController.GetSunDirection());
            cmd.SetGlobalFloat("_SunHeight",     src.lightHeight);
            cmd.SetGlobalFloat("_AmbientNormal", ambientNormal);
            // Blit instead of DrawMesh — DrawMesh silently fails to write to
            // the temp RT for some cameras (e.g. BackgroundCamera without PixelPerfectCamera).
            cmd.Blit(null, LightRTId, sunMat);
        }

        // ── 4. Multiply-blit light RT onto the scene ─────────────────────────
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
