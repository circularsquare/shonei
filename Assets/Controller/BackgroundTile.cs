using UnityEngine;

// World-spanning sprite on the Background layer (sorting order -10, behind tiles)
// showing a tileable texture behind tiles with hasBackground=true.
// Tiles without a background are transparent (sky shows through).
//
// Uses BackgroundTile shader: _MainTex = low-res mask (nx x ny, opaque where background),
// _WallTex = tileable 16x16 texture at 1 rep per tile.
// Tagged Universal2D so it participates in normal lighting (sun, torches, sky light).
public class BackgroundTile : MonoBehaviour {
    public static BackgroundTile instance { get; private set; }

    [SerializeField] GameObject spriteGo;    // BackgroundTileSprite child — set in Inspector
    [SerializeField] SpriteRenderer spriteSR; // SpriteRenderer on spriteGo — set in Inspector

    World world;
    Texture2D maskTex; // RGBA32, nx x ny — mask (opaque where background, transparent where sky)
    Sprite maskSprite;
    Material wallMat;
    bool dirty;

    void Awake() {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
    }

    // Called by WorldController/SaveSystem after tiles are ready.
    // The BackgroundTile GameObject must exist in the scene (under Lighting).
    public static void InitializeWorld(World world) {
        if (instance == null) {
            Debug.LogError("BackgroundTile: no instance in scene. Add a BackgroundTile component to a GameObject under Lighting.");
            return;
        }
        instance.Initialize(world);
    }

    void Initialize(World world) {
        if (this.world != null) {
            for (int x = 0; x < this.world.nx; x++)
                for (int y = 0; y < this.world.ny; y++) {
                    Tile t = this.world.GetTileAt(x, y);
                    t.UnregisterCbBackgroundChanged(OnChanged);
                    t.UnregisterCbTileTypeChanged(OnChanged);
                }
        }
        if (maskTex != null) Destroy(maskTex);

        this.world = world;
        int nx = world.nx;
        int ny = world.ny;

        maskTex = new Texture2D(nx, ny, TextureFormat.RGBA32, false);
        maskTex.filterMode = FilterMode.Point;
        maskTex.wrapMode = TextureWrapMode.Clamp;

        RebuildMaskTexture();
        CreateSprite();

        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++) {
                Tile t = world.GetTileAt(x, y);
                t.RegisterCbBackgroundChanged(OnChanged);
                t.RegisterCbTileTypeChanged(OnChanged);
            }
    }

    void OnChanged(Tile t) {
        dirty = true;
    }

    void LateUpdate() {
        if (dirty) {
            dirty = false;
            RebuildMaskTexture();
        }
    }

    // ── Background mask texture ─────────────────────────────────────────────
    // hasBackground tiles → opaque white. Everything else → transparent.
    // The BackgroundTile shader uses this as a mask; actual appearance comes from _WallTex.
    void RebuildMaskTexture() {
        if (world == null) return;
        int nx = world.nx;
        int ny = world.ny;
        Color32 clear = new Color32(0, 0, 0, 0);
        var pixels = new Color32[nx * ny];

        for (int y = 0; y < ny; y++) {
            for (int x = 0; x < nx; x++) {
                Tile t = world.GetTileAt(x, y);
                if (!t.hasBackground) {
                    pixels[y * nx + x] = clear;
                    continue;
                }
                // Green channel: 255 = top tile (tile above has no background)
                bool isTop = (y + 1 >= ny) || !world.GetTileAt(x, y + 1).hasBackground;
                pixels[y * nx + x] = new Color32(255, (byte)(isTop ? 255 : 0), 255, 255);
            }
        }

        maskTex.SetPixels32(pixels);
        maskTex.Apply();
    }

    // ── World-spanning sprite ───────────────────────────────────────────────
    // Sprite GO is pre-created in the scene (Background layer, sorting order -10).
    // This method creates the dynamic sprite/material and assigns them at runtime.
    void CreateSprite() {
        if (spriteGo == null || spriteSR == null) {
            Debug.LogError("BackgroundTile: spriteGo or spriteSR not assigned in Inspector.");
            return;
        }

        int nx = world.nx;
        int ny = world.ny;
        maskSprite = Sprite.Create(maskTex, new Rect(0, 0, nx, ny), Vector2.zero, 1f);

        Shader wallShader = Shader.Find("Custom/BackgroundTile");
        if (wallShader == null) {
            Debug.LogError("BackgroundTile: Custom/BackgroundTile shader not found");
            return;
        }
        wallMat = new Material(wallShader);

        Texture2D wallTex = Resources.Load<Texture2D>("Sprites/Tiles/undergroundwall");
        if (wallTex == null)
            Debug.LogError("BackgroundTile: wall texture not found at Resources/Sprites/Tiles/undergroundwall");
        else
            wallMat.SetTexture("_WallTex", wallTex);

        Texture2D wallTopTex = Resources.Load<Texture2D>("Sprites/Tiles/undergroundwalltop");
        if (wallTopTex == null)
            Debug.LogError("BackgroundTile: wall top texture not found at Resources/Sprites/Tiles/undergroundwalltop");
        else
            wallMat.SetTexture("_WallTopTex", wallTopTex);

        // Set wall textures as globals so NormalsCaptureBackground.shader can read them
        // (override materials don't inherit per-material properties).
        if (wallTex != null) Shader.SetGlobalTexture("_BackgroundTex", wallTex);
        if (wallTopTex != null) Shader.SetGlobalTexture("_BackgroundTopTex", wallTopTex);

        spriteSR.sprite = maskSprite;
        spriteSR.material = wallMat;
    }

    void OnDestroy() {
        if (maskTex != null) Destroy(maskTex);
    }
}
