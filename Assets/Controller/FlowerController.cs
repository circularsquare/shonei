using System.Collections.Generic;
using UnityEngine;

// Decorative scatter — flowers on grass, mushrooms / moss underground.
// Distinct from the chunked grass-overlay (atlas swap on the tile body)
// and the Plant Structure system (carries growth / harvest / WOM weight);
// decorations are visual-only.
//
// Design — placement zones, per-zone density, deterministic
// (x, y, worldSeed) spawning so reload reproduces the same layout with
// no save data, reactive lifecycle via per-tile callbacks, wind sway
// via FlowerType.windEffect on PlantSprite, future "promote to Plant
// on click" upgrade path — lives in SPEC-rendering.md §Decorative scatter.
//
// Name is historical: this controller handles ALL decoration zones, not
// just flowers. Adding a zone is a JSON FlowerType.placement value +
// a GetZone() case, not a new controller.
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

    // A placed flower: its GameObject + the variant it rolled. The variant is tracked so
    // the layout can be persisted (and restored exactly) — see GatherSave / RestoreFlowers.
    struct FlowerEntry { public GameObject go; public FlowerType type; }

    World world;
    int worldSeed;
    Transform flowersRoot;
    bool subscribed;

    // (x, y) → spawned flower. Encoded long key to avoid Tile refs (cheap, and we'd risk
    // holding refs across a ClearWorld pass).
    readonly Dictionary<long, FlowerEntry> flowers = new Dictionary<long, FlowerEntry>();

    // Saved layout stashed by SaveSystem on load (before the next OnWorldReady), so the load
    // path restores the exact saved flowers instead of re-deriving from live grass/snow state.
    FlowerSaveData[] pendingRestore;

    // ── Seasonal regrowth lifecycle (OnHourElapsed) ──────────────────────
    // Surface flowers are a living layer: out of season they die back at
    // random and in season they re-seed up to FlowerType.maxCount. Mirrors
    // WildHerbSystem in shape. Underground decorations (no `seasons`, no
    // `maxCount`) opt out and stay the static worldgen scatter they always were.
    //
    // Like WildHerbSystem, this uses a plain (non-deterministic) RNG and holds
    // NO save data — the live flower set IS the state and is persisted via
    // GatherSave/RestoreFlowers, so a reload restores the population and the
    // lifecycle simply continues evolving it.
    System.Random lifeRng = new System.Random();

    // Per-hour chance each OUT-OF-SEASON flower dies back. An in-game hour is
    // ~20 s (ticksInDay/24), so ~0.10/hour ≈ a 5% chance per 10 s — a staggered
    // die-back over the first day or two of the off-season, not an instant wipe.
    const float CullChancePerHour = 0.10f;

    // Flowers re-seeded per in-game hour across all under-cap in-season types.
    // Kept a slow trickle so a winter-wiped meadow visibly grows back in over
    // the early part of spring rather than popping in all at once.
    const int RegrowPerHour = 2;

    // Random columns probed per re-seed before giving up (much of the map is
    // ineligible — caves, water, built-over tiles).
    const int PlacementAttempts = 40;

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
            // Only structural callbacks — flowers are a persisted layer that must NOT react
            // to grass/snow drift (that drift is RNG-driven and diverges across a reload, which
            // is exactly what made flowers look different on load). They're removed only when
            // their host tile is destroyed (mined) or built over.
            for (int x = 0; x < world.nx; x++) {
                for (int y = 0; y < world.ny; y++) {
                    Tile t = world.GetTileAt(x, y);
                    t.RegisterCbTileTypeChanged(OnTileTypeChanged);
                    t.RegisterCbStructChanged(OnTileStructChanged);
                }
            }
            subscribed = true;
        }

        if (Db.flowerTypesCount == 0) {
            Debug.LogWarning("FlowerController: no flower types loaded (flowersDb.json empty or missing). Nothing will spawn.");
            pendingRestore = null;
            return;
        }

        // Load path: restore the exact saved layout (even if empty — a world saved with no
        // flowers stays bare). Skips the eligibility scan entirely so the result can't drift
        // with grass/snow that evolved differently since the save. Null (old saves / never
        // saved) falls through to a fresh scatter.
        if (pendingRestore != null) {
            RestoreFlowers(pendingRestore);
            pendingRestore = null;
            return;
        }

        // Fresh world: scatter once from the worldgen-seeded grass. This set is the canonical
        // layout and gets persisted; it no longer grows as grass spreads later.
        for (int x = 0; x < world.nx; x++) {
            for (int y = 0; y < world.ny; y++) {
                Reevaluate(world.GetTileAt(x, y));
            }
        }
    }

    // ── Structural removal (no spawning — see OnWorldReady) ──────────────

    // A flower is dropped only when its host tile is destroyed: mining the tile itself
    // (type → non-solid) or placing a structure on the air tile above it. Two-tile reach:
    // this tile (its own host) AND the tile below (whose "tile above" just changed).
    void OnTileTypeChanged(Tile t) {
        if (world == null) return;
        RemoveIfInvalid(t);
        Tile below = world.GetTileAt(t.x, t.y - 1);
        if (below != null) RemoveIfInvalid(below);
    }

    void OnTileStructChanged(Tile t) {
        if (world == null) return;
        RemoveIfInvalid(t);
        Tile below = world.GetTileAt(t.x, t.y - 1);
        if (below != null) RemoveIfInvalid(below);
    }

    void RemoveIfInvalid(Tile t) {
        long key = Key(t.x, t.y);
        if (flowers.ContainsKey(key) && !IsValidFlowerHost(t)) Despawn(key);
    }

    // The structural half of GetZone: a solid tile with empty, unbuilt air above. Deliberately
    // omits the grass / snow / depth gates — those are decorative drift we no longer react to.
    bool IsValidFlowerHost(Tile t) {
        if (t == null || t.type == null || !t.type.solid) return false;
        Tile above = world.GetTileAt(t.x, t.y + 1);
        if (above == null) return false;
        if (above.type != null && above.type.solid) return false;
        if (AnyStructAt(above)) return false;
        return true;
    }

    // True if any structure occupies `t` at any depth slot — building, platform,
    // foreground (ladder/rope/sign), road, power shaft, or greenhouse frame. A
    // decoration anchored to the air tile above its host would visually overlap
    // any of these, so an occupied air tile disqualifies the spot. (Was a
    // depth-0-only check; widened so flowers never clip platforms/shafts/etc.)
    static bool AnyStructAt(Tile t) {
        if (t == null) return false;
        for (int d = 0; d < Tile.NumDepths; d++)
            if (t.structs[d] != null) return true;
        return false;
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
        if (AnyStructAt(above)) return PlacementZone.None;

        int[] sY = world.surfaceY;
        bool sYValid = sY != null && t.x >= 0 && t.x < sY.Length && sY[t.x] >= 0;

        // Surface grass: live, top-edge tuft, snow-free, AND at-or-above the original ground
        // line. The depth gate keeps flowers out of caves (grass that spread inward through a
        // player-carved skylight is suppressed — flowers read as an outdoor signal). NOTE:
        // worldgen's surfaceY is the AIR tile just above the ground (solid + 1), so the topmost
        // grass tile sits at sY-1 — the gate compares against sY-1, not sY, or no surface tile
        // could ever pass. (RecomputeSurfaceY's old-save fallback uses the solid line instead;
        // sY-1 just admits one extra row there, which is harmless.)
        if (t.type.overlay == "grass"
            && t.overlayState == OverlayState.Live
            && (t.overlayMask & 0b1000) != 0
            && !t.snow
            && (!sYValid || t.y >= sY[t.x] - 1)) {
            return PlacementZone.SurfaceGrass;
        }

        // Underground: at least N tiles below the original ground line.
        // world.surfaceY is the air tile just above the original ground per column, captured at
        // worldgen — keeps "underground" tied to natural geometry, not the currently-tallest
        // dirt block in the column (which the player can shift by mining the top off).
        //
        // Additional shelter gates for mushrooms / moss:
        //   - Not vertically exposed to sky (any solid tile above the spot).
        //   - The mushroom's air tile and all 8 neighbours have an intact
        //     background wall — keeps decorations out of cave-mouth tiles
        //     where the wall has been eroded away by SetBackgrounds.
        // backgroundType is immutable after worldgen so the wall check is
        // stable for the session; the sky-exposure check uses the current
        // tile geometry and won't re-fire if the player mines a chimney
        // post-spawn (acceptable — this is a worldgen-look constraint).
        if (sYValid && t.y <= sY[t.x] - undergroundMinDepth
            && !IsVerticallyExposedToSky(t.x, t.y + 1)
            && HasSheltered3x3Background(t.x, t.y + 1)) {
            return PlacementZone.Underground;
        }

        return PlacementZone.None;
    }

    // True if walking straight up from (x, y) reaches the top of the world
    // without encountering a solid tile. "Upward" only — diagonal exposure
    // doesn't count, matching the rule's literal wording.
    bool IsVerticallyExposedToSky(int x, int y) {
        for (int yy = y + 1; yy < world.ny; yy++) {
            Tile t = world.GetTileAt(x, yy);
            if (t == null) return true;
            if (t.type.solid) return false;
        }
        return true;
    }

    // True if the 3x3 of tiles centred on (cx, cy) all carry a background
    // wall. A single missing wall in the neighbourhood (off-map or eroded
    // by SetBackgrounds Pass 2) disqualifies the spot.
    bool HasSheltered3x3Background(int cx, int cy) {
        for (int dx = -1; dx <= 1; dx++) {
            for (int dy = -1; dy <= 1; dy++) {
                Tile t = world.GetTileAt(cx + dx, cy + dy);
                if (t == null) return false;
                if (!t.hasBackground) return false;
            }
        }
        return true;
    }

    // Worldgen scatter: roll the density gate + variant for a freshly-eligible tile, then
    // place it. Variant + visual bits all read from SEPARATE bytes of the per-tile hash —
    // overlapping byte ranges introduce systematic biases (a previous version had density
    // and xOffset both on bits 8-15, giving every flower a mean −4 px offset).
    //   bits 0-7 : density gate   bits 8-15 : variant   bits 16-23 : x offset   bit 24 : mirror
    void Spawn(Tile t, PlacementZone zone) {
        uint h = HashTile(t.x, t.y, worldSeed);
        float density = zone == PlacementZone.SurfaceGrass ? surfaceGrassDensity : undergroundDensity;
        if ((h & 0xFFu) >= (uint)(density * 256f)) return;

        string placement = zone == PlacementZone.SurfaceGrass ? ZoneSurfaceGrass : ZoneUnderground;
        FlowerType ft = PickVariantForPlacement(((h >> 8) & 0xFFu) / 256f, placement);
        if (ft != null) SpawnAt(t, ft);
    }

    // Places a known variant and records it. The deterministic visual bits (x-offset, mirror,
    // phase) come from the per-tile hash, so a restored flower lands identically to the original.
    // Used by both the worldgen scatter (Spawn) and save-restore (RestoreFlowers).
    void SpawnAt(Tile t, FlowerType ft) {
        Sprite sprite = ft.LoadSprite();
        if (sprite == null) return; // missing artwork — already warned in FlowerType

        uint h = HashTile(t.x, t.y, worldSeed);
        // Sub-pixel x offset, snapped to 1/16 (PPU=16). Range ≈ ±0.3 tile — keeps adjacent
        // decorations from sitting in identical positions without overlapping the next tile.
        float xOffset = (((int)((h >> 16) & 0xFFu) - 128) / 256f) * 0.6f;
        xOffset = Mathf.Round(xOffset * 16f) / 16f;

        // Don't jitter toward a solid same-level neighbour: that tile's grass overhangs into
        // this column, and the shifted sprite would clip it. A centered flower (offset 0) never
        // overlaps — the 16x16 sprite stays within its own tile — so we only cancel a shift that
        // points into a solid neighbour. A shift the other way (toward open air) is left alone.
        if (xOffset > 0f && IsSolidTile(t.x + 1, t.y)) xOffset = 0f;
        else if (xOffset < 0f && IsSolidTile(t.x - 1, t.y)) xOffset = 0f;

        // World position: anchored to the air tile above the solid tile. With sprite
        // pivot=Center on a 16x16 PNG the sprite bottom sits flush on the tile top (spans
        // [y+0.5, y+1.5]); baseY = t.y + 0.5 so the cantilever weight ramps bottom→top.
        Vector3 pos = new Vector3(t.x + xOffset, t.y + 1f, 0f);
        float baseY = t.y + 0.5f;
        float phase = ComputeFlowerPhase(t.x, t.y);
        bool flip = ((h >> 24) & 1u) != 0;

        GameObject go = new GameObject($"flower_{ft.name}_{t.x}_{t.y}");
        go.transform.SetParent(flowersRoot, false);
        go.transform.position = pos;

        // Two-SR split only when the variant has visible wind AND its mask found a head.
        // Mushrooms / moss (windEffect=0) and headless flowers stay single-SR.
        Texture2D mask = (ft.windEffect > 0f) ? ft.LoadHeadMask() : null;
        if (mask != null && ft.hasHead) BuildSwayingFlower(go, sprite, ft, baseY, phase, flip, mask);
        else                            BuildSimpleFlower(go, sprite, ft, baseY, phase, flip);

        flowers[Key(t.x, t.y)] = new FlowerEntry { go = go, type = ft };
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
        if (flowers.TryGetValue(key, out FlowerEntry e)) {
            if (e.go != null) Destroy(e.go);
            flowers.Remove(key);
        }
    }

    void DespawnAll() {
        foreach (var kv in flowers) {
            if (kv.Value.go != null) Destroy(kv.Value.go);
        }
        flowers.Clear();
    }

    // ── Seasonal regrowth lifecycle ─────────────────────────────────────
    // Hooked from World.Tick on the hourly cadence (alongside WildHerbSystem).
    // Out-of-season flowers die back at random; in-season under-cap types
    // re-seed a slow trickle. Underground decorations opt out (no seasons / cap).
    public void OnHourElapsed() {
        if (world == null) return;
        CullOutOfSeason();
        TrickleRegrow();
    }

    // Staggered seasonal die-back. Snapshots victim keys first because Despawn
    // mutates the `flowers` dictionary. Year-round types (no `seasons`) are never
    // culled — only flowers whose authored season has passed.
    void CullOutOfSeason() {
        WeatherSystem weather = WeatherSystem.instance;
        List<long> doomed = null;
        foreach (var kv in flowers) {
            FlowerType ft = kv.Value.type;
            if (ft == null) continue;
            if (ft.seasons == null || ft.seasons.Length == 0) continue; // year-round
            if (ft.IsInSeason(weather)) continue;                       // still in season
            if (lifeRng.NextDouble() >= CullChancePerHour) continue;
            (doomed ??= new List<long>()).Add(kv.Key);
        }
        if (doomed == null) return;
        foreach (long key in doomed) Despawn(key);
    }

    // Re-seed up to RegrowPerHour flowers toward their per-species caps, choosing
    // among in-season under-cap types weighted by `weight`.
    void TrickleRegrow() {
        for (int n = 0; n < RegrowPerHour; n++) {
            FlowerType ft = PickUnderCapType();
            if (ft == null) return; // every regrowing type is at cap or out of season
            PlacementZone zone = ft.placement == ZoneUnderground
                ? PlacementZone.Underground : PlacementZone.SurfaceGrass;
            if (TryFindTile(zone, out int hx, out int hy))
                SpawnAt(world.GetTileAt(hx, hy), ft);
        }
    }

    // weight-weighted pick among regrowing types (maxCount > 0) that are in
    // season AND below their live cap. Null if none are eligible.
    FlowerType PickUnderCapType() {
        Dictionary<FlowerType, int> counts = CountLivePerType();
        WeatherSystem weather = WeatherSystem.instance;
        float total = 0f;
        for (int i = 0; i < Db.flowerTypesCount; i++)
            if (UnderCap(Db.flowerTypes[i], counts, weather)) total += Db.flowerTypes[i].weight;
        if (total <= 0f) return null;

        double pick = lifeRng.NextDouble() * total;
        float acc = 0f;
        for (int i = 0; i < Db.flowerTypesCount; i++) {
            FlowerType ft = Db.flowerTypes[i];
            if (!UnderCap(ft, counts, weather)) continue;
            acc += ft.weight;
            if (pick < acc) return ft;
        }
        return null; // FP edge — caller treats as "nothing to spawn"
    }

    static bool UnderCap(FlowerType ft, Dictionary<FlowerType, int> counts, WeatherSystem weather) {
        if (ft == null || ft.maxCount <= 0) return false;
        if (!ft.IsInSeason(weather)) return false;
        if (ft.LoadSprite() == null) return false;
        counts.TryGetValue(ft, out int live);
        return live < ft.maxCount;
    }

    Dictionary<FlowerType, int> CountLivePerType() {
        var counts = new Dictionary<FlowerType, int>();
        foreach (var kv in flowers) {
            FlowerType ft = kv.Value.type;
            if (ft == null) continue;
            counts.TryGetValue(ft, out int c);
            counts[ft] = c + 1;
        }
        return counts;
    }

    // Probe random tiles for an eligible host in `zone` (GetZone) that isn't
    // already decorated. Returns the SOLID host tile coords (the air tile above
    // is where the decoration visually sits). Gives up after PlacementAttempts.
    //
    // SurfaceGrass: only the topmost solid tile of a column can be the host
    // (grass is a one-tile-thick surface), so we scan a random column down to
    // the ground line. Underground: cave-floor hosts are scattered throughout
    // the depth, so we sample random (x, y) cells and let GetZone filter.
    bool TryFindTile(PlacementZone zone, out int hx, out int hy) {
        hx = hy = -1;
        for (int a = 0; a < PlacementAttempts; a++) {
            int cx = lifeRng.Next(0, world.nx);
            if (zone == PlacementZone.SurfaceGrass) {
                for (int gy = world.ny - 1; gy >= 1; gy--) {
                    Tile t = world.GetTileAt(cx, gy);
                    if (t == null || t.type == null || !t.type.solid) continue;
                    if (GetZone(t) != PlacementZone.SurfaceGrass) break;
                    if (flowers.ContainsKey(Key(cx, gy))) break; // already taken
                    hx = cx; hy = gy; return true;
                }
            } else {
                int cy = lifeRng.Next(1, world.ny);
                Tile t = world.GetTileAt(cx, cy);
                if (t == null) continue;
                if (GetZone(t) != zone) continue;
                if (flowers.ContainsKey(Key(cx, cy))) continue;
                hx = cx; hy = cy; return true;
            }
        }
        return false;
    }

    // ── Save / restore (persisted flower layout) ────────────────────────
    // Flowers are persisted rather than re-derived on load: their eligibility depends on live
    // grass/snow state that evolves via the shared RNG and so doesn't reproduce across a reload.
    // SaveSystem stashes the saved layout via StashRestore before the next OnWorldReady.

    public void StashRestore(FlowerSaveData[] saved) { pendingRestore = saved; }

    public FlowerSaveData[] GatherSave() {
        var list = new List<FlowerSaveData>(flowers.Count);
        foreach (var kv in flowers) {
            if (kv.Value.type == null) continue;
            list.Add(new FlowerSaveData {
                x = (int)((kv.Key >> 16) & 0xFFFF),
                y = (int)(kv.Key & 0xFFFF),
                type = kv.Value.type.name,
            });
        }
        return list.ToArray();
    }

    void RestoreFlowers(FlowerSaveData[] saved) {
        foreach (FlowerSaveData fs in saved) {
            if (fs == null || fs.type == null) continue;
            Tile t = world.GetTileAt(fs.x, fs.y);
            if (t == null) continue;
            if (Db.flowerTypeByName != null
                && Db.flowerTypeByName.TryGetValue(fs.type, out FlowerType ft) && ft != null)
                SpawnAt(t, ft);
        }
    }

    // Cleared on world reset (LoadDefault / new game) so a stale layout doesn't survive.
    public void ResetState() {
        DespawnAll();
        pendingRestore = null;
    }

    // True if the tile at (x, y) exists and is solid. Used to keep decoration jitter from
    // shifting a flower into a solid neighbour whose grass overhangs the column boundary.
    bool IsSolidTile(int x, int y) {
        Tile n = world.GetTileAt(x, y);
        return n != null && n.type != null && n.type.solid;
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
    // breaking already-placed sprites. Also skips out-of-season variants so the
    // worldgen scatter never plants something that would immediately die back
    // (e.g. summer daylilies on a spring-start world — they grow in via the
    // regrowth trickle once their season arrives).
    FlowerType PickVariantForPlacement(float normalized01, string placement) {
        WeatherSystem weather = WeatherSystem.instance;
        float totalWeight = 0f;
        for (int i = 0; i < Db.flowerTypesCount; i++) {
            FlowerType ft = Db.flowerTypes[i];
            if (!EligibleVariant(ft, placement, weather)) continue;
            totalWeight += Mathf.Max(0f, ft.weight);
        }
        if (totalWeight <= 0f) return null;

        float target = normalized01 * totalWeight;
        float acc = 0f;
        for (int i = 0; i < Db.flowerTypesCount; i++) {
            FlowerType ft = Db.flowerTypes[i];
            if (!EligibleVariant(ft, placement, weather)) continue;
            acc += Mathf.Max(0f, ft.weight);
            if (target <= acc) return ft;
        }
        return null; // unreachable barring float drift; defensive
    }

    static bool EligibleVariant(FlowerType ft, string placement, WeatherSystem weather) {
        return ft != null
            && ft.placement == placement
            && ft.IsInSeason(weather)
            && ft.LoadSprite() != null;
    }
}
