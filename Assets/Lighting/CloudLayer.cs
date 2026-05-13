using UnityEngine;

// Procedural cloud field. The cloud's _MainTex and _NormalMap textures
// are generated each frame on the GPU via Graphics.Blit through the
// two-pass "Hidden/CloudFieldGen" shader. The result is bound on the
// SpriteRenderer via MaterialPropertyBlock — the rest of the rendering
// pipeline cannot tell the textures are GPU-generated RenderTextures.
//
// ── Shape ──────────────────────────────────────────────────────────────
// The cloud body is a constellation of CPU-spawned 3D sphere-blobs.
// Each frame this script walks a regular grid ANCHORED IN NOISE-SPACE
// (each cell maps to a fixed point in the noise field, not to a fixed
// offset from the camera), samples the same 2D value noise the shader
// uses (see Hash2D / ValueNoise below — must stay in sync with
// Assets/Lighting/Noise.hlsl), and emits a blob wherever density
// exceeds the humidity-driven threshold. Each blob is a
// Vector4(x_local, y_local, z, radius) in world units; the array
// (max 256, typically 30–80 active) is pushed to the gen shader as
// _Blobs + _BlobCount.
//
// Anchoring the grid in noise-space (not sprite-local) is what keeps
// blobs from popping in/out at the viewport edges as the camera pans:
// cell (col=5, row=3) ALWAYS maps to the same noise position, so its
// density / threshold-test result is stable across frames. The blob's
// sprite-local position then scrolls smoothly with the camera (via
// `lx = anchorX − noiseOffset.x`), but the blob's identity (radius,
// z-jitter, jitter offset) doesn't churn.
//
// ── Shading ────────────────────────────────────────────────────────────
// Pass 0 treats the blob array as a HEIGHT FIELD `h(x,y) = max_i(zTop_i)`
// where `zTop_i = blob.z + sqrt(r² − dist²)` for each blob the pixel
// sits inside. The surface normal at each pixel comes from a 5-tap
// finite-difference gradient of this height field (centre + 4 cardinal
// neighbours, step `normalEpsilon`). Adjacent blobs blend automatically
// at their junctions — the L/R or B/T samples may pick different blobs
// and the resulting gradient is a natural mix of their sphere normals,
// so the cloud reads as one continuous body with bumps instead of
// looking like discrete circles glued together. Lambertian against the
// global _SunDir is then 3-band quantized.
//
// Alpha is a metaball-style merged silhouette (sum of per-blob linear
// influences, smoothstep-thresholded). Adjacent overlapping blobs merge
// into a single body rather than showing a bumpy circles-union outline.
//
// Pass 1 writes a FLAT tangent normal (0,0,1) to normalRT, with the
// same metaball alpha as Pass 0 so NormalsCapture's clip lines up with
// the visible silhouette exactly. The project's NormalsCapture pipeline
// reads the flat normal as a camera-facing world normal, so LightSun
// contributes a uniform brightness across the cloud rather than per-
// pixel N·L variation that would smear the discrete colour bands.
// Day/night still works: sun strength × flat-normal-NdotL × ambient
// gives the cloud's overall brightness; sr.color (humidity tint)
// multiplies on top.
//
// ── Performance ────────────────────────────────────────────────────────
// CPU: ~256 cells × cheap-noise eval = ~50 μs/frame. GPU: blob loop is
// O(_BlobCount) per pixel with simple distance/sphere math — typically
// 30–80 iterations of ~6 ops at 32k pixels, well under 1 ms even on
// integrated graphics. The previous CPU-Perlin implementation ran
// ~20k Mathf.PerlinNoise calls per frame and was the source of the
// ~30 fps drop that motivated the original GPU migration.
//
// ── Future shading headroom ────────────────────────────────────────────
// • Cross-blob shadows: small extra march toward sun inside the blob
//   loop, marking the picked normal as occluded if another blob's
//   surface sits between this pixel and the sun.
// • Multi-octave blobs: a second sparser pass with larger radii layered
//   behind smaller blobs — "big puff + small detail" hierarchy.
// • Rim translucency: pixels near the front-most blob's silhouette get
//   a 4th colour band on the sun-facing side (forward scatter).
//
// ── Execution order ────────────────────────────────────────────────────
// LateUpdate at order 100 — runs after SkyCamera's default-order
// LateUpdate (which moves the SkyCamera to follow the main camera).
// At this point cam.transform.position is the sprite's intended world
// origin for the frame; we set the sprite position, generate the blob
// list, push uniforms, and Blit. URP renders the cameras afterwards
// and sees fresh RT contents.
[DefaultExecutionOrder(100)]
public class CloudLayer : MonoBehaviour {
    [Header("Field texture")]
    [Tooltip("Texture dimensions in pixels. Larger = more detail and screen coverage; GPU cost is negligible at these sizes.")]
    public Vector2Int textureSize = new Vector2Int(256, 128);
    [Tooltip("Pixels-per-world-unit. Half the project's main PPU (8 vs 16) gives 2x2 screen pixels per texel — still reads as pixel art.")]
    public float pixelsPerUnit = 8f;

    [Tooltip("World-units → noise-units multiplier. Smaller = larger smooth cloud features; larger = noisier, more broken-up shapes.")]
    public float noiseScale = 0.15f;
    [Tooltip("Horizontal stretch of the noise field. 1 = isotropic. >1 = features wider than tall (canonical horizontal-cumulus shapes). 2 default gives clouds about twice as wide as they are tall.")]
    public float noiseAspect = 2.0f;

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

    [Header("Tint")]
    public Color baseColorClear = Color.white;
    public Color baseColorStorm = new Color(0.40f, 0.42f, 0.48f, 1f);

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
    [Tooltip("Grid cell size in world units. Smaller = denser blob spawn, more solid clouds. Blob radii should overlap adjacent cells (radiusMax ≈ cellSize) so clouds merge visually.")]
    public float blobCellSize    = 1.0f;
    [Tooltip("How much each blob jitters within its cell (0=grid-aligned, 1=full cell). Hashed per cell, deterministic frame-to-frame.")]
    [Range(0f, 1f)] public float blobJitter = 0.5f;
    [Tooltip("Minimum blob radius (world units) — used when density barely exceeds threshold.")]
    public float blobRadiusMin   = 0.7f;
    [Tooltip("Maximum blob radius (world units) — used when density is well above threshold. Should overlap neighbouring cells so cloud bodies look continuous.")]
    public float blobRadiusMax   = 1.4f;
    [Tooltip("Range of z-jitter per blob (world units). Spreads blobs in depth so the front-of-cloud isn't a flat plane; visible as varied silhouette overlap.")]
    public float blobDepthRange  = 0.5f;
    [Tooltip("How many smaller 'detail' blobs to spawn around each parent. 0 = single-scale only; 3-5 gives a fractal cumulus look (big puffs with cauliflower bumps). Each multiplies the active blob count, so the shader's blob loop grows linearly — MAX_BLOBS is 512.")]
    [Range(0, 8)] public int  subBlobCount        = 4;
    [Tooltip("Sub-blob radius as a fraction of its parent's radius. 0.3-0.5 reads as crisp cauliflower; lower values make finer detail. Hashed per-child variation jitters this ±40% so siblings differ in size.")]
    [Range(0.15f, 0.7f)] public float subBlobRadiusFactor = 0.45f;
    [Tooltip("How far each sub-blob sits from its parent's centre, as a fraction of parent radius. ~0.5 = mostly interior detail; ~0.7-0.9 = bumps that bulge out through the parent's silhouette; >1.0 = sub-blobs sit beyond the parent's edge as semi-detached lumps.")]
    [Range(0f, 1.5f)] public float subBlobSpread  = 0.7f;
    [Tooltip("Strength of the per-pixel edge-wobble noise. Perturbs the metaball alpha threshold (not the sampling position), so cloud silhouettes get organically bumpy without the interior shading twisting. In threshold units: ~0.05 = subtle bumps; >0.15 = obviously wavy.")]
    [Range(0f, 0.2f)] public float edgeWobbleStrength = 0.05f;
    [Tooltip("Frequency of the edge-wobble noise (1/world-units). Higher = finer-grained bumps; lower = broader undulations. ~1 gives wobble at roughly one-world-unit scale.")]
    public float edgeWobbleScale = 1.0f;
    [Tooltip("Horizontal stretch of each blob into an ellipsoid. 1 = perfect spheres; 1.5-2 = cumulus-like elongated lobes; >2.5 = obvious cigars. Composes with noiseAspect (which stretches where blobs cluster) — together they shape both the macro and the micro of horizontal cloud appearance.")]
    [Range(1f, 3f)] public float blobAspect = 1.5f;
    [Tooltip("Edge threshold (centre of the metaball alpha smoothstep). Lower = more generous silhouette (blobs read solid further from their centres); higher = tighter cloud bodies. Single-blob silhouettes extend roughly to dist = sqrt(1 - threshold) * radius before the alpha begins fading.")]
    [Range(0.02f, 0.6f)] public float edgeThreshold = 0.2f;
    [Tooltip("Edge softness (half-width of the metaball alpha smoothstep). 0 = razor-hard silhouette; ~0.1 = subtle feather; >0.3 = visibly wispy. Combines with strength of warp noise — softer edges + bigger warp = puffy cumulus, sharper edges = more cartoony.")]
    [Range(0.0f, 0.4f)]  public float edgeSoftness  = 0.15f;

    Camera cam;
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

    // Blob buffer — allocated once at MAX_BLOBS, reused every frame.
    // SetVectorArray locks the array length on first call; reallocation
    // would force a fresh upload of the new size, so the size is fixed.
    // Must match MAX_BLOBS in CloudFieldGen.shader.
    const int MAX_BLOBS = 512;
    readonly Vector4[] blobBuffer = new Vector4[MAX_BLOBS];
    int blobCount;

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
    static readonly int BlobAspectId     = Shader.PropertyToID("_BlobAspect");
    static readonly int EdgeThresholdId  = Shader.PropertyToID("_EdgeThreshold");
    static readonly int EdgeSoftnessId   = Shader.PropertyToID("_EdgeSoftness");
    static readonly int BlobsId          = Shader.PropertyToID("_Blobs");
    static readonly int BlobCountId      = Shader.PropertyToID("_BlobCount");

    void Start() {
        // Reference camera = SkyCamera (the one that actually renders the
        // cloud sprite). Falls back to Camera.main if SkyCamera isn't set up.
        if (SkyCamera.instance != null && SkyCamera.instance.BgCam != null) {
            cam = SkyCamera.instance.BgCam;
        } else {
            cam = Camera.main;
        }

        // Ensure we're on the layer SkyCamera renders. The "Sky" layer is
        // the standard in this project (Unity layer 6, mask 64 on
        // SkyCamera). If a previous reparent / inspector tweak knocked
        // this GameObject onto a different layer, the sprite would fall
        // into a culling gap (Main camera excludes it, SkyCamera doesn't
        // see it) and render in Scene view but not Game view.
        int skyLayer = LayerMask.NameToLayer("Sky");
        if (skyLayer < 0) skyLayer = gameObject.layer;
        gameObject.layer = skyLayer;

        int w = textureSize.x;
        int h = textureSize.y;

        // Procedural RTs. Linear (sRGB=false) is critical for normalRT —
        // sRGB would warp the (rgb*2 - 1) decode in NormalsCapture.shader.
        // Same flags on mainRT for symmetry.
        mainRT   = MakeRT(w, h);
        normalRT = MakeRT(w, h);

        // Dummy backing for Sprite.Create — see field comment.
        spriteTex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false) {
            filterMode = FilterMode.Point,
            hideFlags  = HideFlags.HideAndDontSave,
        };

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

        // Clean any pre-existing scene children (legacy sprite-pool layout).
        for (int i = transform.childCount - 1; i >= 0; i--) {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }

        // FullRect mesh, NOT the default Tight. Tight builds the sprite
        // mesh from texture alpha at Create time — and the dummy backing
        // is all-zeros, so a Tight mesh would have no triangles and the
        // sprite would render nothing forever after.
        var sprite = Sprite.Create(spriteTex, new Rect(0, 0, w, h),
                                   new Vector2(0.5f, 0.5f), pixelsPerUnit,
                                   extrude: 0, meshType: SpriteMeshType.FullRect);

        var srGo = new GameObject("CloudFieldSprite");
        srGo.transform.SetParent(transform, worldPositionStays: false);
        srGo.layer = gameObject.layer;
        sr = SpriteMaterialUtil.AddSpriteRenderer(srGo);
        sr.sprite = sprite;
        sr.sortingLayerName = "Background";
        sr.sortingOrder = 0;

        // Bind both procedural RTs via MPB. Override _MainTex (normally
        // auto-bound to the sprite's source texture) so the SpriteRenderer
        // samples the procedural mainRT instead of the empty dummy.
        // Get-modify-set so any other auto-bound properties survive.
        mpb = new MaterialPropertyBlock();
        sr.GetPropertyBlock(mpb);
        mpb.SetTexture(MainTexId,   mainRT);
        mpb.SetTexture(NormalMapId, normalRT);
        sr.SetPropertyBlock(mpb);

        // Plant the sprite at its intended world position immediately so
        // the first-frame render is correct before LateUpdate runs.
        Vector3 startCam = cam.transform.position;
        float startSpriteY = startCam.y + (bandCenterY - startCam.y) * worldLockingY;
        sr.transform.position = new Vector3(startCam.x, startSpriteY, renderZ);
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

    void LateUpdate() {
        if (cam == null || sr == null || cloudGenMat == null) return;

        // Accumulate wind into a horizontal noise offset.
        float wind = WeatherSystem.instance != null ? WeatherSystem.instance.wind : 0f;
        windOffsetX += wind * windDriftScale * Time.deltaTime;

        // Position the sprite. Horizontal: locked to camera x (parallax
        // comes from the noise offset, not sprite motion). Vertical:
        // sprite tracks camera y with worldLockingY interpolating between
        // sky-locked (=0) and world-locked (=1) — worldLockingY=0.25 gives
        // 25% vertical parallax. renderZ must sit between SkyCamera's
        // near (0.3) and far planes.
        Vector3 camPos = cam.transform.position;
        float spriteY = camPos.y + (bandCenterY - camPos.y) * worldLockingY;
        sr.transform.position = new Vector3(camPos.x, spriteY, renderZ);

        // Humidity-driven full-sprite tint, multiplied by _MainTex.rgb in
        // the sprite shader.
        float humidity = WeatherSystem.instance != null
            ? WeatherSystem.instance.humidity : WeatherSystem.humidityMean;
        sr.color = Color.Lerp(baseColorClear, baseColorStorm, humidity);

        // Defensive: a domain reload (script recompile while in Play
        // mode) can leave the RT object alive but with its GPU contents
        // dropped, so re-Create before the Blit. Cheap when already
        // created.
        if (!mainRT.IsCreated())   mainRT.Create();
        if (!normalRT.IsCreated()) normalRT.Create();

        // Compute the noise-offset (parallax × camera-x + accumulated wind
        // drift) — used by GenerateBlobs to convert noise-anchored blob
        // positions to sprite-local coordinates. The shader no longer
        // needs it directly; blob spawning fully replaces the per-pixel
        // noise sampling that used it before.
        Vector2 noiseOffset = new Vector2(camPos.x * worldLockingX + windOffsetX, 0f);
        float threshold = Mathf.Lerp(thresholdClear, thresholdStorm, humidity);

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
        cloudGenMat.SetFloat (BlobAspectId,     blobAspect);
        cloudGenMat.SetFloat (EdgeThresholdId,  edgeThreshold);
        cloudGenMat.SetFloat (EdgeSoftnessId,   edgeSoftness);

        // Generate the blob list for this frame and push to the shader.
        // Blob sprite-local y is always (anchorY - bandCenterY) — i.e.,
        // their offset within the band. The sprite itself moves with
        // parallax (via spriteY above); the blobs ride along.
        GenerateBlobs(threshold, noiseOffset);
        cloudGenMat.SetVectorArray(BlobsId, blobBuffer);
        cloudGenMat.SetInt(BlobCountId, blobCount);

        // Pass 0 → mainRT (blob-shaded 3-band colour + soft union mask),
        // Pass 1 → normalRT (flat tangent normal so the global lightmap
        // contributes uniform brightness across the cloud).
        Graphics.Blit(null, mainRT,   cloudGenMat, 0);
        Graphics.Blit(null, normalRT, cloudGenMat, 1);
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
        float cellSz = Mathf.Max(0.01f, blobCellSize);

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

                // Noise sample at the anchored point. noiseAspect divides
                // the x scale so features stretch horizontally — at
                // aspect=2 the x noise frequency is half, so cloud
                // clusters end up about twice as wide as they are tall.
                float d = ValueNoise(anchorX * noiseScale / noiseAspect,
                                     anchorY * noiseScale) * band;
                float excess = d - threshold;
                if (excess <= 0f) continue;

                // Two stacked lerps:
                //   1) "density → max radius" maps how-far-above-threshold
                //      to where in [blobRadiusMin, blobRadiusMax] the
                //      blob's full radius sits. Saturates above +0.3.
                //   2) "fade-in" multiplier ramps radius from 0 at the
                //      threshold to full over fadeRange. This is what
                //      kills the popping that the user saw: when a cell's
                //      noise value or the threshold drifts (humidity
                //      shifting, wind grazing near-threshold cells), the
                //      blob smoothly grows from a point rather than
                //      flipping on at full size.
                const float fadeRange = 0.05f;
                float fadeIn = Mathf.Clamp01(excess / fadeRange);
                float maxR   = Mathf.Lerp(blobRadiusMin, blobRadiusMax,
                                           Mathf.Clamp01(excess / 0.3f));
                float radius = maxR * fadeIn;
                // Skip blobs too small to contribute visibly. Frees up
                // the MAX_BLOBS slot for cells that actually matter.
                if (radius < blobRadiusMin * 0.2f) continue;

                float z = (Hash2D(col * 5.19f, row * 9.71f) - 0.5f) * blobDepthRange;
                blobBuffer[blobCount++] = new Vector4(lx, lyBand, z, radius);
                if (blobCount >= MAX_BLOBS) return;

                // Sub-blob spawn: cluster a handful of smaller blobs
                // around this parent so the cloud reads as fractal
                // cumulus (big puffs + cauliflower bumps) rather than a
                // single-scale field of identical spheres. Positions and
                // sizes are hashed per (parent cell, sub index) so the
                // result is deterministic per noise state — sub-blobs
                // ride along with the parent through wind / parallax
                // without independently popping in or out.
                for (int s = 0; s < subBlobCount; s++) {
                    float ha = Hash2D(col * 23.7f + s * 1.31f, row * 41.9f + s * 0.7f);
                    float hd = Hash2D(col * 13.1f + s * 5.31f, row *  7.9f + s * 1.3f);
                    float hr = Hash2D(col * 31.1f + s * 2.31f, row * 19.9f + s * 2.7f);
                    float hz = Hash2D(col * 17.1f + s * 3.31f, row * 29.9f + s * 3.1f);

                    float angle = ha * 6.28318530718f;            // 2π
                    float dist  = hd * subBlobSpread * radius;    // 0 = at parent centre, 1*radius = at parent edge

                    float subLx = lx     + Mathf.Cos(angle) * dist;
                    float subLy = lyBand + Mathf.Sin(angle) * dist;
                    // Sub-blob radius: subBlobRadiusFactor * parent.radius,
                    // hash-modulated ±40% so siblings differ in size for
                    // an organic look rather than a regular ring of clones.
                    float subR  = radius * subBlobRadiusFactor * (0.6f + hr * 0.8f);
                    // Slight extra z-jitter for sub-blobs, half the
                    // parent's range — sits them at slightly different
                    // depths than parent without breaking the cluster.
                    float subZ  = z + (hz - 0.5f) * blobDepthRange * 0.5f;

                    blobBuffer[blobCount++] = new Vector4(subLx, subLy, subZ, subR);
                    if (blobCount >= MAX_BLOBS) return;
                }
            }
        }
    }

    // ── 2D value noise — port of Noise.hlsl::Hash2D / ValueNoise ───────
    // Must stay bit-compatible with the shader so blob spawn positions
    // match the visible cloud body. If you change Noise.hlsl, change
    // these too (and vice versa). Both are short and stable enough that
    // drift risk is low; the duplication buys us trivially-cheap CPU
    // sampling for the spawn grid.

    static float Frac(float x) {
        return x - Mathf.Floor(x);
    }

    static float Hash2D(float px, float py) {
        float ax = Frac(px * 123.34f);
        float ay = Frac(py * 456.21f);
        float d  = ax * (ax + 45.32f) + ay * (ay + 45.32f);
        return Frac((ax + d) * (ay + d));
    }

    static float ValueNoise(float px, float py) {
        float ix = Mathf.Floor(px), iy = Mathf.Floor(py);
        float fx = px - ix,         fy = py - iy;
        float a = Hash2D(ix,     iy);
        float b = Hash2D(ix + 1, iy);
        float c = Hash2D(ix,     iy + 1);
        float d = Hash2D(ix + 1, iy + 1);
        float ux = fx * fx * (3f - 2f * fx);
        float uy = fy * fy * (3f - 2f * fy);
        return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy);
    }
}
