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
public class BackgroundLayer : MonoBehaviour {
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

    [Header("Render order")]
    [Tooltip("Sorting order within the Background sorting layer. -100 = sky gradient, -50 = stars, 0 = clouds. -75 puts the background between stars and clouds (in front of stars).")]
    public int sortingOrder = -75;


    Camera cam;
    SpriteRenderer sr;
    Material mat;
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
    // Shared 1×1 flat-normal texture, so the NormalsCapture pass sees a
    // camera-facing normal for every background pixel and LightSun
    // contributes uniform brightness (no spurious per-pixel sun shading).
    static Texture2D _flatNormalTex;

    static readonly int MainTexId        = Shader.PropertyToID("_MainTex");
    static readonly int NormalMapId      = Shader.PropertyToID("_NormalMap");
    static readonly int ViewportSizeId   = Shader.PropertyToID("_ViewportSize");
    static readonly int CameraPosId      = Shader.PropertyToID("_CameraPos");
    static readonly int BandCenterYId    = Shader.PropertyToID("_BandCenterY");
    static readonly int ParallaxOffsetId = Shader.PropertyToID("_ParallaxOffset");
    static readonly int TexUVScaleId     = Shader.PropertyToID("_TexUVScale");

    void Start() {
        if (SkyCamera.instance != null && SkyCamera.instance.BgCam != null) {
            cam = SkyCamera.instance.BgCam;
        } else {
            cam = Camera.main;
        }

        int skyLayer = LayerMask.NameToLayer("Sky");
        if (skyLayer < 0) skyLayer = gameObject.layer;
        gameObject.layer = skyLayer;

        if (texture == null) {
            Debug.LogError("BackgroundLayer: no texture assigned — disabling.");
            enabled = false;
            return;
        }

        // The parallax-baked RT — both visible and lighting passes sample
        // this at native UV. Sized to match the camera's actual pixel
        // dimensions (re-sized in LateUpdate if those change) so each
        // RT pixel corresponds to a screen pixel — no downsampling.
        // Linear (sRGB=false) for consistency with the cloud's RTs.
        bgRT = MakeBgRT(Mathf.Max(1, cam.pixelWidth), Mathf.Max(1, cam.pixelHeight));

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

        Shader sh = Resources.Load<Shader>("Shaders/BackgroundLayer");
        if (sh == null) {
            Debug.LogError("BackgroundLayer: missing shader Resources/Shaders/BackgroundLayer.shader — disabling.");
            enabled = false;
            return;
        }
        mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };

        Shader genSh = Resources.Load<Shader>("Shaders/BackgroundLayerGen");
        if (genSh == null) {
            Debug.LogError("BackgroundLayer: missing shader Resources/Shaders/BackgroundLayerGen.shader — disabling.");
            enabled = false;
            return;
        }
        bgGenMat = new Material(genSh) { hideFlags = HideFlags.HideAndDontSave };

        for (int i = transform.childCount - 1; i >= 0; i--) {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }

        var srGo = new GameObject("BackgroundLayerSprite");
        srGo.transform.SetParent(transform, worldPositionStays: false);
        srGo.layer = gameObject.layer;
        sr = srGo.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingLayerName = "Background";
        sr.sortingOrder = sortingOrder;
        sr.material = mat;

        // MPB-bind the parallax-baked RT as _MainTex (overriding the
        // sprite-auto-bound spriteTex), and a flat-normal 1×1 as
        // _NormalMap. The flat normal makes NormalsCapture see a uniform
        // camera-facing normal everywhere, so LightSun adds constant
        // brightness rather than per-pixel varying sun shading.
        mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(MainTexId,   bgRT);
        mpb.SetTexture(NormalMapId, GetFlatNormalTex());
        sr.SetPropertyBlock(mpb);

        sr.transform.position = new Vector3(cam.transform.position.x, cam.transform.position.y, renderZ);
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

    static Texture2D GetFlatNormalTex() {
        if (_flatNormalTex != null) return _flatNormalTex;
        _flatNormalTex = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp,
            hideFlags  = HideFlags.HideAndDontSave,
        };
        // (0.5, 0.5, 1.0) decodes via (rgb*2 - 1) to (0, 0, 1) — a
        // tangent-space normal pointing straight at the camera.
        _flatNormalTex.SetPixel(0, 0, new Color(0.5f, 0.5f, 1.0f, 1.0f));
        _flatNormalTex.Apply();
        return _flatNormalTex;
    }

    void OnDestroy() {
        if (bgRT != null)     { bgRT.Release(); bgRT = null; }
        if (mat != null)      Destroy(mat);
        if (bgGenMat != null) Destroy(bgGenMat);
        if (spriteTex != null) Destroy(spriteTex);
    }

    void LateUpdate() {
        if (cam == null || sr == null || mat == null || bgGenMat == null) return;

        Vector3 camPos = cam.transform.position;

        // Cover the SkyCamera's viewport regardless of zoom. The sprite's
        // native size is 1×1 wu (from the 64×64 dummy at PPU=64) — scale
        // up by viewport extent so any visible pixel is on the quad.
        float viewH = cam.orthographicSize * 2f;
        float viewW = viewH * cam.aspect;
        sr.transform.position = new Vector3(camPos.x, camPos.y, renderZ);
        sr.transform.localScale = new Vector3(viewW, viewH, 1f);

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

        // Push gen-shader uniforms then bake the parallax-shifted view
        // into bgRT. The Blit's source supplies _MainTex (the user
        // texture); the rest of the per-pixel math happens in the gen
        // shader's fragment.
        bgGenMat.SetVector(ViewportSizeId,   new Vector2(viewW, viewH));
        bgGenMat.SetVector(CameraPosId,      new Vector2(camPos.x, camPos.y));
        bgGenMat.SetFloat (BandCenterYId,    bandCenterY);
        bgGenMat.SetVector(ParallaxOffsetId, parallaxOffset);
        bgGenMat.SetVector(TexUVScaleId,     texUVScale);

        // Keep bgRT at exact camera-pixel resolution so the visible
        // sprite samples it 1:1 (no upscaling blur). If the camera's
        // pixel size changed (window resize, EvenResolutionEnforcer
        // adjustment, etc.), re-allocate and re-bind via MPB.
        int targetW = Mathf.Max(1, cam.pixelWidth);
        int targetH = Mathf.Max(1, cam.pixelHeight);
        if (bgRT.width != targetW || bgRT.height != targetH) {
            bgRT.Release();
            Destroy(bgRT);
            bgRT = MakeBgRT(targetW, targetH);
            sr.GetPropertyBlock(mpb);
            mpb.SetTexture(MainTexId,   bgRT);
            mpb.SetTexture(NormalMapId, GetFlatNormalTex());
            sr.SetPropertyBlock(mpb);
        }

        if (!bgRT.IsCreated()) bgRT.Create();
        // Explicit _MainTex bind on the gen material before the Blit.
        // Graphics.Blit's source-arg binding occasionally doesn't reach
        // a custom material's TEXTURE2D(_MainTex) sampler — explicitly
        // setting it here removes the dependency on that implicit hook.
        bgGenMat.SetTexture(MainTexId, texture);
        Graphics.Blit(texture, bgRT, bgGenMat);

        // Sorting order can change at runtime; sync it.
        sr.sortingOrder = sortingOrder;

        // Day/night tint is the user's explicit knob. LightSun's
        // contribution to the lightmap handles overall brightness.
        sr.color = tint;
    }
}
