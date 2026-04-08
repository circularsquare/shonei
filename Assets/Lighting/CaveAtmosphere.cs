using UnityEngine;

// Manages underground visual atmosphere: background cave walls and sky exposure
// for ambient light attenuation.
//
// Background wall: a world-spanning sprite on the Default layer (sorting order -10,
// behind tiles) showing dark gray behind tiles with hasBackgroundWall=true.
// Tiles without a background wall are transparent (sky shows through).
//
// Sky exposure: a small texture (nx x ny, R8) encoding per-tile ambient light.
// Tiles WITHOUT a background wall get full ambient; tiles WITH one get none.
// Uploaded as a global shader texture for the LightAmbientFill pass to sample.
public class CaveAtmosphere : MonoBehaviour {
    public static CaveAtmosphere instance { get; private set; }

    World world;
    Texture2D bgWallTex;     // RGBA32, nx x ny — mask (opaque where wall, transparent where sky)
    Texture2D exposureTex;   // R8, nx x ny — 255 where no background wall, 0 where wall
    Sprite bgWallSprite;
    GameObject bgWallGo;
    Material wallMat;
    bool dirtyWall;
    bool dirtyExposure;

    // Auto-creates the singleton if it doesn't exist yet.
    // Called by WorldController/SaveSystem after tiles are ready.
    public static void InitializeWorld(World world) {
        if (instance == null) {
            var go = new GameObject("CaveAtmosphere");
            instance = go.AddComponent<CaveAtmosphere>();
        }
        instance.Initialize(world);
    }

    void Initialize(World world) {
        // Unsubscribe from old world if re-initializing (load/reset).
        if (this.world != null) {
            for (int x = 0; x < this.world.nx; x++)
                for (int y = 0; y < this.world.ny; y++) {
                    Tile t = this.world.GetTileAt(x, y);
                    t.UnregisterCbBackgroundWallChanged(OnBackgroundWallChanged);
                    t.UnregisterCbTileTypeChanged(OnTileChanged);
                }
        }
        if (bgWallGo != null) Destroy(bgWallGo);
        if (exposureTex != null) Destroy(exposureTex);
        if (bgWallTex != null) Destroy(bgWallTex);

        this.world = world;
        int nx = world.nx;
        int ny = world.ny;

        // Sky exposure texture — driven by hasBackgroundWall.
        exposureTex = new Texture2D(nx, ny, TextureFormat.R8, false);
        exposureTex.filterMode = FilterMode.Point;
        exposureTex.wrapMode = TextureWrapMode.Clamp;

        // Background wall texture.
        bgWallTex = new Texture2D(nx, ny, TextureFormat.RGBA32, false);
        bgWallTex.filterMode = FilterMode.Point;
        bgWallTex.wrapMode = TextureWrapMode.Clamp;

        RebuildExposureTexture();
        RebuildBgWallTexture();
        CreateBgWallSprite();

        // Subscribe to changes.
        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                t.RegisterCbBackgroundWallChanged(OnBackgroundWallChanged);
                t.RegisterCbTileTypeChanged(OnTileChanged);
            }
    }

    void OnBackgroundWallChanged(Tile t) {
        dirtyWall = true;
        dirtyExposure = true;
    }

    // Tile type changes can affect whether solid sprites cover the wall,
    // but since we always show the wall color for hasBackgroundWall tiles,
    // we don't strictly need to rebuild. Keep it for future flexibility.
    void OnTileChanged(Tile t) {
        dirtyWall = true;
    }

    void LateUpdate() {
        if (dirtyExposure) {
            dirtyExposure = false;
            RebuildExposureTexture();
        }
        if (dirtyWall) {
            dirtyWall = false;
            RebuildBgWallTexture();
        }
    }

    // ── Sky exposure texture ─────────────────────────────────────────────────
    // Tiles without a background wall get full sky exposure (255).
    // Tiles with a background wall get none (0).
    void RebuildExposureTexture() {
        if (world == null) return;
        int nx = world.nx;
        int ny = world.ny;
        byte[] exposureBytes = new byte[nx * ny];

        for (int y = 0; y < ny; y++) {
            for (int x = 0; x < nx; x++) {
                Tile t = world.GetTileAt(x, y);
                exposureBytes[y * nx + x] = (byte)(t.hasBackgroundWall ? 0 : 255);
            }
        }

        exposureTex.LoadRawTextureData(exposureBytes);
        exposureTex.Apply();

        Shader.SetGlobalTexture("_SkyExposureTex", exposureTex);
        Shader.SetGlobalVector("_GridSize", new Vector4(nx, ny, 0, 0));
    }

    // ── Background wall mask texture ────────────────────────────────────────
    // hasBackgroundWall tiles → opaque white. Everything else → transparent.
    // The CaveWall shader uses this as a mask; actual appearance comes from _WallTex.
    void RebuildBgWallTexture() {
        if (world == null) return;
        int nx = world.nx;
        int ny = world.ny;
        Color32 solid = new Color32(255, 255, 255, 255);
        Color32 clear = new Color32(0, 0, 0, 0);
        var pixels = new Color32[nx * ny];

        for (int y = 0; y < ny; y++) {
            for (int x = 0; x < nx; x++) {
                Tile t = world.GetTileAt(x, y);
                pixels[y * nx + x] = t.hasBackgroundWall ? solid : clear;
            }
        }

        bgWallTex.SetPixels32(pixels);
        bgWallTex.Apply();
    }

    // ── World-spanning sprite on Default layer ─────────────────────────────
    // Sorting order -10 keeps it behind tiles (order 0). Participates in
    // normal lighting (sun, torches) but receives no sky ambient because
    // _SkyExposureTex is 0 for hasBackgroundWall tiles.
    // Uses CaveWall shader: _MainTex = mask, _WallTex = tileable wall texture.
    void CreateBgWallSprite() {
        int nx = world.nx;
        int ny = world.ny;
        bgWallSprite = Sprite.Create(bgWallTex, new Rect(0, 0, nx, ny), Vector2.zero, 1f);

        bgWallGo = new GameObject("CaveBackground");
        bgWallGo.transform.position = new Vector3(-0.5f, -0.5f, 0f);

        Shader wallShader = Shader.Find("Custom/CaveWall");
        if (wallShader == null) {
            Debug.LogError("CaveAtmosphere: Custom/CaveWall shader not found");
            return;
        }
        wallMat = new Material(wallShader);

        Texture2D wallTex = Resources.Load<Texture2D>("Sprites/Tiles/undergroundwall");
        if (wallTex == null)
            Debug.LogError("CaveAtmosphere: wall texture not found at Resources/Sprites/Tiles/undergroundwall");
        else
            wallMat.SetTexture("_WallTex", wallTex);

        SpriteRenderer sr = bgWallGo.AddComponent<SpriteRenderer>();
        sr.sprite = bgWallSprite;
        sr.material = wallMat;
        sr.sortingOrder = -10;
    }

    void OnDestroy() {
        if (exposureTex != null) Destroy(exposureTex);
        if (bgWallTex != null) Destroy(bgWallTex);
    }
}
