using System.Collections.Generic;
using UnityEngine;

// Decorative scatter (flowers on grass, mushrooms / moss underground).
// Deliberately separate from the chunked grass-overlay system (which is an
// atlas swap on the tile body, see TileMeshController) AND from the Plant
// Structure system (which carries gameplay state — growth, harvest, work
// orders).
//
// Why a third path: these decorations need per-instance variation (different
// sprite, different x-offset, swaying independently, optional mirror), which
// the chunked overlay can't express, but they don't deserve the save /
// Structure registry / WOM weight that Plants carry. Spawning is fully
// deterministic from (x, y, worldSeed), so reload-from-save reproduces the
// exact same layout without persisting anything.
//
// ── Placement zones ────────────────────────────────────────────────────
// Each FlowerType declares its `placement` string. The controller maps each
// tile to a `PlacementZone` and only considers variants whose placement
// matches.
//   SurfaceGrass     — solid grass-overlay tile, Live, U-bit set, no snow,
//                      air above. The original flower domain.
//   Underground  — any solid tile at least `undergroundMinDepth`
//                      below the *original* land surface (cached at init,
//                      not re-derived from current geometry — keeps "below
//                      the natural surface" stable even as the player digs
//                      new caves). Air above, no structure above.
//
// Each zone has its own density tunable so the surface meadow and the cave
// floor decorate at independent rates.
//
// ── Spawn lifecycle ────────────────────────────────────────────────────
//   - OnWorldReady(world, seed) is called by WorldController at the tail
//     end of Start(), after both the worldgen and load paths have settled.
//     It computes the per-column surfaceY cache, does a full-world scan,
//     and subscribes per-tile callbacks (once).
//   - cbTileTypeChanged: re-evaluate this tile AND the tile below (whose
//     "tile above" check may have just changed).
//   - cbOverlayChanged: re-evaluate this tile (grass live/dying/dead, mask).
//   - cbSnowChanged: re-evaluate this tile (snow on/off).
//
// ── Wind sway ──────────────────────────────────────────────────────────
//   Decorations share the PlantSprite shader with Plant.cs. Sway amplitude
//   is attenuated per-variant by FlowerType.windEffect (1 = full sway like
//   plants, 0 = rigid for mushrooms / moss), threaded through to the shader
//   as the `_SwayAmount` MPB property. No per-frame controller work.
//
// ── Future upgrade path ────────────────────────────────────────────────
//   If decorations become harvestable, the natural step is to look up a
//   matching PlantType by name on player click and replace the decorative
//   GameObject with a real Plant instance. The FlowerType / Plant data-model
//   split lets the visual layer continue to back the decoration for
//   un-clicked instances.
public class FlowerController : MonoBehaviour {
    public static FlowerController instance { get; private set; }

    // Fraction of eligible surface-grass tiles that bloom. Per-tile decision
    // is a deterministic hash compare against this threshold, so the same
    // seed reproduces the same layout. Tunable in inspector for iteration.
    [Range(0f, 1f)] public float surfaceGrassDensity = 0.18f;

    // Underground rate — meaningfully sparser than the surface meadow because
    // every dirt tile in a cave column would otherwise be a candidate. ~4%
    // gives a "fairly rare" feel without leaving caves visibly empty.
    [Range(0f, 1f)] public float undergroundDensity = 0.04f;

    // Minimum tiles below the original land surface for the Underground
    // zone to apply. 5 keeps a sensible buffer so a player who digs one tile
    // down doesn't immediately see cave mushrooms grow into their starter pit.
    [Min(1)] public int undergroundMinDepth = 5;

    // sortingOrder for decorations — see SPEC-rendering.md §"Sorting orders".
    // Sits above the tile body (0..−4), the snow layer (2), and the chunked
    // grass overlay (11), and below platforms (15), animals (~50), plants
    // (60), and floor items (70).
    public const int FlowerSortingOrder = 12;

    // String identifiers for `FlowerType.placement` in JSON. Keep authored
    // entries readable — enums on JSON deserialize awkwardly with Newtonsoft.
    public const string ZoneSurfaceGrass    = "surface_grass";
    public const string ZoneUnderground = "underground";

    enum PlacementZone { None, SurfaceGrass, Underground }

    World world;
    int worldSeed;
    Transform flowersRoot;
    bool subscribed;

    // (x, y) → spawned flower GameObject. Encoded long key to avoid Tile
    // refs (cheap, and we'd risk holding refs across a ClearWorld pass).
    readonly Dictionary<long, GameObject> flowers = new Dictionary<long, GameObject>();

    void Awake() {
        if (instance != null) {
            Debug.LogError("FlowerController: more than one instance");
        }
        instance = this;
    }

    // Single entry point used by both new-world and save-load paths. Idempotent:
    // calling twice (or after a ClearWorld) despawns everything and rebuilds from
    // scratch with the current seed. Tile callbacks are subscribed only once
    // because the World/Tile objects survive across world resets.
    public void OnWorldReady(World w, int seed) {
        world = w;
        worldSeed = seed;
        if (flowersRoot == null) {
            var rootGo = new GameObject("Flowers");
            rootGo.transform.SetParent(transform, false);
            flowersRoot = rootGo.transform;
        }

        DespawnAll();

        if (!subscribed) {
            for (int x = 0; x < world.nx; x++) {
                for (int y = 0; y < world.ny; y++) {
                    Tile t = world.GetTileAt(x, y);
                    t.RegisterCbTileTypeChanged(OnTileTypeChanged);
                    t.RegisterCbOverlayChanged(OnTileChanged);
                    t.RegisterCbSnowChanged(OnTileChanged);
                    t.RegisterCbStructChanged(OnTileStructChanged);
                }
            }
            subscribed = true;
        }

        if (Db.flowerTypesCount == 0) {
            Debug.LogWarning("FlowerController: no flower types loaded (flowersDb.json empty or missing). Nothing will spawn.");
            return;
        }
        // Full scan. Quick enough at world sizes ≤200×200: at most a handful
        // of comparisons per tile, plus the few percent that actually spawn.
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Reevaluate(world.GetTileAt(x, y));
            }
        }
    }

    // ── Reactive callbacks ──────────────────────────────────────────────

    void OnTileChanged(Tile t) {
        // World ref isn't valid until OnWorldReady has run at least once.
        // Filter out the noisy initial setup pass before that point.
        if (world == null) return;
        Reevaluate(t);
    }

    // Type changes affect both this tile (its own eligibility) AND the tile
    // below (whose "tile above must be empty" check just flipped). The
    // alternative would be to re-evaluate the whole column, but two tiles
    // is enough — eligibility never reaches further than the immediate
    // neighbour above.
    void OnTileTypeChanged(Tile t) {
        if (world == null) return;
        Reevaluate(t);
        Tile below = world.GetTileAt(t.x, t.y - 1);
        if (below != null) Reevaluate(below);
    }

    // Struct changes have the same two-tile reach as type changes: a building
    // landing on an air tile blocks the flower on the solid tile below (so the
    // tile-below check matters), and a road/shaft on the solid tile itself
    // ends its eligibility directly (so the tile-itself check matters).
    void OnTileStructChanged(Tile t) {
        if (world == null) return;
        Reevaluate(t);
        Tile below = world.GetTileAt(t.x, t.y - 1);
        if (below != null) Reevaluate(below);
    }

    // ── Eligibility + spawn/despawn ─────────────────────────────────────

    void Reevaluate(Tile t) {
        long key = Key(t.x, t.y);
        PlacementZone zone = GetZone(t);
        bool has = flowers.ContainsKey(key);
        if (zone != PlacementZone.None && !has) Spawn(t, zone);
        else if (zone == PlacementZone.None && has) Despawn(key);
        // If the zone changed (e.g. dirt → grass) we'd ideally despawn the
        // current variant and roll a fresh pick. In practice the only path
        // for that is a worldgen-time transient; runtime tile type changes
        // (mining) take a tile out of all zones rather than between them.
        // Leaving this as a known minor edge case.
    }

    PlacementZone GetZone(Tile t) {
        if (t == null) return PlacementZone.None;
        if (t.type == null || !t.type.solid) return PlacementZone.None;
        Tile above = world.GetTileAt(t.x, t.y + 1);
        if (above == null) return PlacementZone.None;
        if (above.type != null && above.type.solid) return PlacementZone.None;
        if (above.structs[0] != null) return PlacementZone.None;

        int[] sY = world.surfaceY;
        bool sYValid = sY != null && t.x >= 0 && t.x < sY.Length && sY[t.x] >= 0;

        // Surface grass: live, top-edge tuft, snow-free, AND at-or-above the
        // original ground line. The depth gate keeps flowers strictly out of
        // caves: even if grass somehow grew on a tile below the surface (e.g.
        // the player carved a skylight letting grass spread inward), we
        // suppress flowers there — flowers should read as an outdoor signal.
        if (t.type.overlay == "grass"
            && t.overlayState == OverlayState.Live
            && (t.overlayMask & 0b1000) != 0
            && !t.snow
            && (!sYValid || t.y >= sY[t.x])) {
            return PlacementZone.SurfaceGrass;
        }

        // Underground: at least N tiles below the original ground line.
        // world.surfaceY is the topmost solid tile per column captured at
        // worldgen — keeps "underground" tied to natural geometry, not the
        // currently-tallest dirt block in the column (which the player can
        // shift by mining the top off).
        if (sYValid && t.y <= sY[t.x] - undergroundMinDepth) {
            return PlacementZone.Underground;
        }

        return PlacementZone.None;
    }

    void Spawn(Tile t, PlacementZone zone) {
        uint h = HashTile(t.x, t.y, worldSeed);

        // Each independent random property reads from a SEPARATE byte of the
        // 32-bit hash. Overlapping byte ranges introduce systematic biases —
        // a previous version had density and xOffset both reading bits 8-15,
        // which produced a mean −4 px offset on every flower in the world.
        //   bits 0-7   : density gate
        //   bits 8-15  : variant pick
        //   bits 16-23 : sub-pixel x offset
        //   bit  24    : horizontal mirror
        float density = zone == PlacementZone.SurfaceGrass ? surfaceGrassDensity : undergroundDensity;
        uint bDensity = h & 0xFFu;
        if (bDensity >= (uint)(density * 256f)) return;

        string placement = zone == PlacementZone.SurfaceGrass ? ZoneSurfaceGrass : ZoneUnderground;
        FlowerType ft = PickVariantForPlacement(((h >> 8) & 0xFFu) / 256f, placement);
        if (ft == null) return;

        Sprite sprite = ft.LoadSprite();
        if (sprite == null) return; // missing artwork — already warned in FlowerType

        // Sub-pixel x offset, snapped to 1/16 (PPU=16). Range ≈ ±0.3 tile.
        // Keeps adjacent decorations from sitting in identical positions
        // without drifting so far they overlap into the next tile.
        float xOffset = (((int)((h >> 16) & 0xFFu) - 128) / 256f) * 0.6f;
        xOffset = Mathf.Round(xOffset * 16f) / 16f;

        // World position: anchored to the air tile above the solid tile.
        // With sprite pivot=Center on a 16x16 PNG, this puts the sprite
        // bottom flush against the tile's top edge (sprite spans
        // [y+0.5, y+1.5]). _PlantBaseY = t.y + 0.5 (sprite bottom) so the
        // cantilever weight ramps from 0 at the bottom of the sprite to 1
        // at the top — the natural bend a viewer expects.
        Vector3 pos = new Vector3(t.x + xOffset, t.y + 1f, 0f);
        float baseY = t.y + 0.5f;
        float phase = ComputeFlowerPhase(t.x, t.y);
        bool flip = ((h >> 24) & 1u) != 0;

        GameObject go = new GameObject($"flower_{ft.name}_{t.x}_{t.y}");
        go.transform.SetParent(flowersRoot, false);
        go.transform.position = pos;

        // Two-SR split only kicks in when the variant has visible wind AND
        // its auto-detected mask actually found a head region. Mushrooms /
        // moss with windEffect=0 stay single-SR (no point splitting a thing
        // that doesn't move). All-green flowers without a head also stay
        // single-SR (LoadHeadMask returns null → hasHead stays false).
        Texture2D mask = (ft.windEffect > 0f) ? ft.LoadHeadMask() : null;
        if (mask != null && ft.hasHead) {
            BuildSwayingFlower(go, sprite, ft, baseY, phase, flip, mask);
        } else {
            BuildSimpleFlower(go, sprite, ft, baseY, phase, flip);
        }

        flowers[Key(t.x, t.y)] = go;
    }

    // Single-SR path — the original behaviour, used by rigid decorations
    // (mushrooms / moss) and by flowers whose mask didn't detect a head.
    void BuildSimpleFlower(GameObject parent, Sprite sprite, FlowerType ft, float baseY, float phase, bool flip) {
        SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(parent);
        var plantMat = SpriteMaterialUtil.PlantSpriteMaterial;
        if (plantMat != null) sr.sharedMaterial = plantMat;
        sr.sprite = sprite;
        sr.sortingOrder = FlowerSortingOrder;
        sr.flipX = flip;
        LightReceiverUtil.SetSortBucket(sr);
        LightReceiverUtil.SetPlantSwayMPB(sr, baseY, 1f, phase, useMask: false, swayAmount: ft.windEffect);
    }

    // Two-SR path — stem + head share the same sprite, distinguished by the
    // _SwayMask companion. The stem keeps the regular per-vertex weighted
    // bend; the head's vertices all shift by the cantilever amount at
    // _HeadCenterY so it visually translates as one rigid chunk.
    //
    // Head SR sorts one above stem so where the bent stem-top overlaps with
    // the translated head, the head wins. flipX is applied to BOTH halves so
    // they stay aligned; mirroring also flips the mask sample (Unity's flipX
    // inverts the sprite mesh UV, so the mask in the same SR sees the same
    // flipped lookup — keeps the discard regions consistent).
    void BuildSwayingFlower(GameObject parent, Sprite sprite, FlowerType ft, float baseY, float phase, bool flip, Texture2D mask) {
        GameObject stemGo = new GameObject("stem");
        stemGo.transform.SetParent(parent.transform, false);
        SpriteRenderer stemSr = SpriteMaterialUtil.AddSpriteRenderer(stemGo);
        var plantMat = SpriteMaterialUtil.PlantSpriteMaterial;
        if (plantMat != null) stemSr.sharedMaterial = plantMat;
        stemSr.sprite = sprite;
        stemSr.sortingOrder = FlowerSortingOrder;
        stemSr.flipX = flip;
        LightReceiverUtil.SetSortBucket(stemSr);
        LightReceiverUtil.SetPlantSwayMPB(stemSr, baseY, 1f, phase,
            useMask: true, swayAmount: ft.windEffect,
            roleIsHead: false, headCenterY: ft.headCenterY, maskTexture: mask);

        GameObject headGo = new GameObject("head");
        headGo.transform.SetParent(parent.transform, false);
        SpriteRenderer headSr = SpriteMaterialUtil.AddSpriteRenderer(headGo);
        if (plantMat != null) headSr.sharedMaterial = plantMat;
        headSr.sprite = sprite;
        headSr.sortingOrder = FlowerSortingOrder + 1;
        headSr.flipX = flip;
        LightReceiverUtil.SetSortBucket(headSr);
        LightReceiverUtil.SetPlantSwayMPB(headSr, baseY, 1f, phase,
            useMask: true, swayAmount: ft.windEffect,
            roleIsHead: true, headCenterY: ft.headCenterY, maskTexture: mask);
    }

    void Despawn(long key) {
        if (flowers.TryGetValue(key, out GameObject go)) {
            if (go != null) Destroy(go);
            flowers.Remove(key);
        }
    }

    void DespawnAll() {
        foreach (var kv in flowers) {
            if (kv.Value != null) Destroy(kv.Value);
        }
        flowers.Clear();
    }

    // ── Determinism helpers ─────────────────────────────────────────────

    // Long key for (x, y). 16 bits each is plenty (world is ≤200 tiles per
    // axis in practice). Top bits left clear; debug-friendly.
    static long Key(int x, int y) {
        return ((long)(uint)x << 16) | (uint)y;
    }

    // Cheap mixing hash. Mixes seed in with x, y so different worlds get
    // different distributions; the constants are arbitrary odd numbers
    // chosen to scramble bit patterns evenly across the hash space.
    // Not cryptographic — we just need decorrelation between adjacent tiles
    // so neighboring decorations don't all draw the same variant.
    static uint HashTile(int x, int y, int seed) {
        unchecked {
            uint h = (uint)seed;
            h ^= (uint)x * 0x9E3779B1u;
            h ^= (uint)y * 0x85EBCA77u;
            h *= 0xC2B2AE3Du;
            h ^= h >> 16;
            h *= 0x27D4EB2Fu;
            h ^= h >> 15;
            return h;
        }
    }

    // Same shape as Plant.ComputePlantPhase but with a slightly different
    // constant pair, so an unintentional phase-lock between an adjacent
    // plant and flower isn't visible at idle wind.
    static float ComputeFlowerPhase(int x, int y) {
        return (x * 23 + y * 37) * 0.11f;
    }

    // Weighted pick across loaded FlowerTypes filtered to a single placement.
    // `normalized01` is a deterministic fraction in [0, 1) from the per-tile
    // hash — same seed → same variant. Skips variants whose sprite is missing
    // so authors can ship JSON entries before the art is finished without
    // breaking already-placed sprites.
    FlowerType PickVariantForPlacement(float normalized01, string placement) {
        float totalWeight = 0f;
        for (int i = 0; i < Db.flowerTypesCount; i++) {
            FlowerType ft = Db.flowerTypes[i];
            if (ft == null) continue;
            if (ft.placement != placement) continue;
            if (ft.LoadSprite() == null) continue;
            totalWeight += Mathf.Max(0f, ft.weight);
        }
        if (totalWeight <= 0f) return null;

        float target = normalized01 * totalWeight;
        float acc = 0f;
        for (int i = 0; i < Db.flowerTypesCount; i++) {
            FlowerType ft = Db.flowerTypes[i];
            if (ft == null) continue;
            if (ft.placement != placement) continue;
            if (ft.LoadSprite() == null) continue;
            acc += Mathf.Max(0f, ft.weight);
            if (target <= acc) return ft;
        }
        return null; // unreachable barring float drift; defensive
    }
}
