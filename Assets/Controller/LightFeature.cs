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
//   3. Draw directional LightSources (sun) fullscreen — NdotL from _CapturedNormalsRT.
//   4. Draw point LightSources (torches) as circle quads — NdotL × radial falloff.
//   5. Multiply-blit light RT onto scene: final = scene × lightmap.
public class LightFeature : ScriptableRendererFeature {
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
        renderer.EnqueuePass(capturePass);
        lightPass.Setup(renderer.cameraColorTarget);
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

    public NormalsCapturePass() {
        mat = CoreUtils.CreateEngineMaterial("Hidden/NormalsCapture");
    }

    public void Dispose() => CoreUtils.Destroy(mat);

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd) {
        var desc = rd.cameraData.cameraTargetDescriptor;
        desc.depthBufferBits = 0;
        cmd.GetTemporaryRT(CapturedNormalsId, desc, FilterMode.Point);
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData rd) {
        if (mat == null) return;
        var cmd = CommandBufferPool.Get("NormalsCapture");
        cmd.SetRenderTarget(CapturedNormalsId);
        cmd.ClearRenderTarget(false, true, Color.clear); // black = flat fallback in light shaders
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);

        // Draw all Universal2D sprites with the normals capture override material.
        var drawSettings = CreateDrawingSettings(
            new ShaderTagId("Universal2D"), ref rd, SortingCriteria.CommonTransparent);
        drawSettings.overrideMaterial          = mat;
        drawSettings.overrideMaterialPassIndex = 0;
        var filterSettings = new FilteringSettings(RenderQueueRange.all);
        context.DrawRenderers(rd.cullResults, ref drawSettings, ref filterSettings);
    }

    public override void OnCameraCleanup(CommandBuffer cmd) {
        cmd.ReleaseTemporaryRT(CapturedNormalsId);
    }
}

// ── Light Pass ────────────────────────────────────────────────────────────────

class LightPass : ScriptableRenderPass, System.IDisposable {
    static readonly int LightRTId = Shader.PropertyToID("_CustomLightRT");

    // Ambient normal floor — softens back-face darkness on point and directional lights.
    // Exposed as a constant here; could be made a public field on LightFeature if needed.
    const float AmbientNormal = 0.15f;

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

    public void Setup(RenderTargetIdentifier colorBuffer) {
        this.colorBuffer = colorBuffer;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData rd) {
        var desc = rd.cameraData.cameraTargetDescriptor;
        desc.depthBufferBits = 0;
        cmd.GetTemporaryRT(LightRTId, desc, FilterMode.Bilinear);
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData rd) {
        if (circleMat == null || sunMat == null || compositeMat == null) return;

        var cmd = CommandBufferPool.Get("CustomLighting");

        var view = rd.cameraData.GetViewMatrix();
        var proj = GL.GetGPUProjectionMatrix(rd.cameraData.GetProjectionMatrix(), renderIntoTexture: false);
        cmd.SetViewMatrix(view);
        cmd.SetProjectionMatrix(proj);

        // ── 1. Clear light RT to ambient color ───────────────────────────────
        cmd.SetRenderTarget(LightRTId);
        Color ambient = SunController.GetAmbientColor();
        cmd.ClearRenderTarget(false, true, ambient);

        // ── 2. Draw directional lights (sun) ─────────────────────────────────
        var sunMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(10000f, 10000f, 1f));
        foreach (var src in LightSource.all) {
            if (src == null || !src.isDirectional || src.intensity <= 0f) continue;
            cmd.SetGlobalColor("_SunColor",      src.lightColor);
            cmd.SetGlobalFloat("_SunIntensity",  src.intensity);
            cmd.SetGlobalVector("_SunDir",       SunController.GetSunDirection());
            cmd.SetGlobalFloat("_AmbientNormal", AmbientNormal);
            cmd.DrawMesh(quad, sunMatrix, sunMat, 0, 0, null);
        }

        // ── 3. Draw point lights (torches, lanterns, etc.) ───────────────────
        var mpb = new MaterialPropertyBlock();
        foreach (var src in LightSource.all) {
            if (src == null || src.isDirectional) continue;
            mpb.SetColor("_LightColor",    src.lightColor);
            mpb.SetFloat("_Intensity",     src.intensity);
            mpb.SetFloat("_InnerFraction",
                src.outerRadius > 0f ? src.innerRadius / src.outerRadius : 0f);
            mpb.SetVector("_LightWorldPos", (Vector4)src.transform.position);
            mpb.SetFloat("_LightHeight",   src.lightHeight);
            mpb.SetFloat("_AmbientNormal", AmbientNormal);

            float d = src.outerRadius * 2f;
            var matrix = Matrix4x4.TRS(
                new Vector3(src.transform.position.x, src.transform.position.y, 0f),
                Quaternion.identity,
                new Vector3(d, d, 1f));

            cmd.DrawMesh(quad, matrix, circleMat, 0, 0, mpb);
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
