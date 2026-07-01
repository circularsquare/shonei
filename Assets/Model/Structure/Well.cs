using System.Collections.Generic;
using UnityEngine;

// Well — a 1-wide shaft dug straight down into solid ground (soil or stone) by a digger,
// resized with Q/E at placement (shapeIndex → shaft depth, see `digsSolidColumn`). Like the
// burrow it KEEPS its footprint tiles solid (`preservesTile`): the surrounding terrain edges
// stay clean and the interior reads dark underground. The dug-out look + opaque dark backdrop
// come from the preserved-tile facade, NOT a mined-out hole — which is why a solid-tile shaft
// can't use the natural per-tile water sim (that only renders in non-solid tiles).
//
// Geometry: the bottom ny-1 tiles are the shaft (dug into solid); the top tile (dy=ny-1) is the
// open surface wellhead the hauler walks up to. The well holds a single pooled-water RESERVOIR
// (`storedWater`, 0..Capacity) it owns directly — it pulls groundwater from surrounding wet soil
// and seeps back into dry soil (Tick), and draws the pool with the shared container-liquid render
// path (TryGetDisplayLiquid) over an interior zone synthesised across the whole shaft column.
// Haulers draw water with a lowering bucket (Phase 4).
public class Well : Building {
    // ── Reservoir + render geometry tuning ──────────────────────────
    // Shaft interior cavity in the well sprite (the darker central channel between the stone
    // walls): water renders only over these columns so the walls aren't painted blue. Derived
    // from well_s.png (walls at lx 2-4 / 11-13, cavity at lx 5-10).
    const int InteriorLxMin = 5;
    const int InteriorLxMax = 10;
    // The bottom shaft tile keeps a 2px floor lip, so its water tops out at 14px (≈140 units)
    // rather than a full 16px — matches the sprite's floor and the "bottom holds less" intent.
    const int FloorLipPx = 2;

    // Groundwater exchange (per second, per qualifying neighbour). The give/take pivot is HALF the
    // neighbour's own moisture capacity: above it the neighbour gives water to the reservoir, below
    // it the reservoir seeps back. Scaling by cap (not a fixed 50) lets wells work in low-capacity
    // stone — a granite tile (cap 20) pivots at 10, not an unreachable 50.
    // Moisture-equivalent of one stored-water SIM unit (the well stores water in sim units, so it
    // converts at the per-unit granularity). Tied to MoistureSystem so a well, a pond, and the
    // farmer's bottled water all value water identically; the authored rate is MoistureSystem.MoisturePerLiang.
    const int MoisturePerWaterUnit = MoistureSystem.MoisturePerWaterUnit;
    // Gradual exchange: soil-moisture a single wet/dry neighbour gives/takes per second. Small so the
    // reservoir fills and seeps over minutes instead of snapping full. Sub-unit water is carried in
    // _moistureDebt so low rates don't truncate to zero at the 10:1 conversion. Kept gentle to roughly
    // match the tile-diffusion feel (was 3, which drew/seeped noticeably faster). TUNABLE — raise for
    // faster wells, lower for slower.
    const int MoistureFlowPerNeighbourPerSec = 1;

    // Pooled water, in tile-water units (same scale as WaterController.WaterMax, 160 = one full
    // tile). Persisted. Mutated by the groundwater economy and by hauler draws.
    public int storedWater;

    // Sub-water-unit moisture carried between ticks so the gentle per-second exchange doesn't
    // truncate to zero at the 10:1 conversion. Signed in moisture units: + = pulled-in moisture
    // awaiting conversion to a stored-water unit; - = water already spent seeping, awaiting
    // conversion back. Always |.|<MoisturePerWaterUnit; small enough not to bother persisting.
    int _moistureDebt;

    // ── Static registry (mirrors Elevator) ──────────────────────────
    static readonly List<Well> _all = new();

    // Reset for Reload-Domain-off play mode so repeated Play presses don't carry orphaned refs.
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStaticsForPlayMode() {
        _all.Clear();
        _whiteSprite = null;   // dead texture ref across play sessions — recreate lazily
    }

    // Per-second groundwater exchange for every well. Called from World.Tick after the generic
    // moisture pass. Snapshot in case a Tick triggers a Destroy that mutates _all.
    public static void TickAll() {
        if (_all.Count == 0) return;
        var snapshot = _all.ToArray();
        foreach (var w in snapshot) w.Tick();
    }

    public Well(StructType st, int x, int y, bool mirrored = false, int shapeIndex = 0)
        : base(st, x, y, mirrored, shapeIndex: shapeIndex) {
        BuildWaterZone();
        _all.Add(this);
    }

    public override void Destroy() {
        AbortDraw();
        _all.Remove(this);
        base.Destroy();
    }

    // The hauler works from the wellhead (top of the column), not the bottom anchor — override the
    // default fixed-offset work tile to the surface piece so the drawer stands where it can be reached.
    public override Tile workTile => World.instance.GetTileAt(x, y + Shape.ny - 1);

    // Haulers only draw once the reservoir holds a worthwhile buffer (more than MinDrawableLiang),
    // so a near-empty well isn't constantly worked for a trickle. Gates both the craft dispatch
    // (ConditionsMet) and the task itself (DrawWaterTask.Initialize).
    public const int MinDrawableLiang = 10;
    public bool HasDrawableWater => storedWater > WaterController.LiangToWaterUnits(MinDrawableLiang);
    public override bool ConditionsMet() => HasDrawableWater;

    // Reservoir capacity in tile-water units: one full tile per shaft tile. The shaft is the
    // bottom ny-1 dug tiles; the top tile is the open surface wellhead, not a water cell.
    public int Capacity => (Shape.ny - 1) * WaterController.WaterMax;

    // Reservoir contents in fen of the `water` item — converted from sim units via the shared
    // WaterController economy (full tile = LiangPerFullTile liang), so a bucket draw and the info
    // readout stay consistent with pumps and ponds.
    public int StoredWaterFen => WaterController.WaterUnitsToFen(storedWater);
    public int CapacityFen    => WaterController.WaterUnitsToFen(Capacity);

    // ── Reservoir rendering (container-liquid path) ─────────────────
    // Synthesise the decorative-water zone across the whole shaft column. The base ctor only
    // scans the anchor sprite (one tile) for a {name}_w mask, which can't express a variable-height
    // shaft — so we build the interior cavity offsets directly. Offsets are local to the anchor
    // (bottom) tile; WaterController.RegisterDecorativeWater converts them to world pixels.
    void BuildWaterZone() {
        int shaftTiles = Shape.ny - 1;
        if (shaftTiles <= 0) { waterPixelOffsets = null; return; }
        const int p = 16;                        // pixels per tile (sprite PPU)
        int topLocalY = shaftTiles * p - 1;     // top of the shaft column, in local pixels
        var offs = new List<Vector2Int>((InteriorLxMax - InteriorLxMin + 1) * (topLocalY - FloorLipPx + 1));
        for (int ly = FloorLipPx; ly <= topLocalY; ly++)
            for (int lx = InteriorLxMin; lx <= InteriorLxMax; lx++)
                offs.Add(new Vector2Int(lx, ly));
        waterPixelOffsets = offs;
    }

    // Fill the shaft zone to the current reservoir level. Default tint (alpha 0) → the shader's
    // water blue; surfaceRow shimmers the meniscus like a pond top.
    public override bool TryGetDisplayLiquid(out float fillFraction, out Color32 tint, out bool surfaceRow) {
        tint         = default;   // alpha 0 → default water blue
        surfaceRow   = true;
        fillFraction = Capacity > 0 ? (float)storedWater / Capacity : 0f;
        return fillFraction > 0f;
    }

    // ── Groundwater economy ─────────────────────────────────────────
    // Exchange water with the solid soil/stone in and around the shaft: the left and right wall
    // columns at every shaft depth, the floor below, and the well's OWN (preserved-solid) footprint
    // column — those tiles stay solid and hold groundwater too, so the well taps the soil it sits
    // in, not just its borders. Wet tiles feed the reservoir; dry ones draw from it.
    void Tick() {
        World w = World.instance;
        if (w == null) return;
        int shaftTiles = Shape.ny - 1;
        for (int dy = 0; dy < shaftTiles; dy++) {
            ExchangeWith(w.GetTileAt(x - 1, y + dy));   // left wall
            ExchangeWith(w.GetTileAt(x + 1, y + dy));   // right wall
        }
        // The well's own footprint column — its preserved-solid tiles (shaft + wellhead) hold
        // groundwater too, so tap the full height, not just the borders.
        for (int dy = 0; dy < Shape.ny; dy++)
            ExchangeWith(w.GetTileAt(x, y + dy));
        ExchangeWith(w.GetTileAt(x, y - 1));   // floor below the shaft
    }

    void ExchangeWith(Tile t) {
        if (t == null || !t.type.solid) return;   // only earth/stone holds groundwater
        int threshold = t.type.moistureCapacity / 2;   // give/take pivot scales with the neighbour's capacity
        // Reservoir contents in moisture units, including the sub-unit debt — bounds the exchange so
        // we never overfill or over-drain mid-tick.
        int eff  = storedWater * MoisturePerWaterUnit + _moistureDebt;
        int capM = Capacity * MoisturePerWaterUnit;
        if (t.moisture > threshold && eff < capM) {
            // Pull groundwater up: take a little of the neighbour's excess, never past the threshold.
            int m = Mathf.Min(MoistureFlowPerNeighbourPerSec, Mathf.Min(t.moisture - threshold, capM - eff));
            t.moisture    = (byte)(t.moisture - m);
            _moistureDebt += m;
        } else if (t.moisture < threshold && eff > 0) {
            // Seep back into dry soil: give a little, never past the threshold or below empty.
            int m = Mathf.Min(MoistureFlowPerNeighbourPerSec, Mathf.Min(threshold - t.moisture, eff));
            t.moisture    = (byte)(t.moisture + m);
            _moistureDebt -= m;
        }
        // Settle whole tile-water units out of the accumulated moisture debt.
        while (_moistureDebt >= MoisturePerWaterUnit)  { storedWater++; _moistureDebt -= MoisturePerWaterUnit; }
        while (_moistureDebt <= -MoisturePerWaterUnit) { storedWater--; _moistureDebt += MoisturePerWaterUnit; }
    }

    // ── Bucket draw (hauler water fetch) ─────────────────────────────
    // A hauler stands at the wellhead and lowers a bucket on a rope to the water surface, waits,
    // then raises it with water. Time scales with how deep the water is (an emptier well = deeper
    // surface = longer draw). The state machine advances per tick (deterministic, like the
    // elevator); WellBucket lerps the bucket + rope sprites per frame toward bucketY.
    public enum DrawPhase { Idle, Lowering, Soaking, Raising }
    public DrawPhase drawPhase { get; private set; } = DrawPhase.Idle;
    public bool IsDrawing => drawPhase != DrawPhase.Idle;

    // Bucket model position, in tile units relative to the anchor (dy=0) — wellhead at ny-1, water
    // surface lower. WellBucket reads this to place the bucket sprite (visual lerps toward it).
    public float bucketY { get; private set; }

    Animal _drawAnimal;
    DrawWaterObjective _drawObjective;
    float _soakElapsed;
    // Water units removed from the reservoir when the bucket reached the bottom and started soaking,
    // held until the bucket is raised and handed to the hauler. Refunded if the draw aborts mid-flight.
    int _drawnWater;

    public const float BucketSpeed = 4f / 3f;           // tiles/SECOND (3× slower than the original 4)
    const float SoakSeconds = 1f;                       // ~1s submerged
    // The bucket centre dips this far past the surface so it submerges; clamped so its base rests on
    // the shaft floor when the well is near-empty (BucketHalfTiles = half the 4px bucket sprite).
    const float BucketSubmergeDip = 0.125f;
    const float BucketHalfTiles   = 0.125f;
    const int   DrawLiang = 5;                          // liang per bucket

    // Local-Y (tiles, relative to anchor) the bucket rests at when up — the wellhead tile.
    public float WellheadLocalY => Shape.ny - 1;

    // Local-Y (tiles, relative to anchor) of the current water surface, matching the rendered fill
    // (pixel-accurate: FloorLip + filled pixel rows, /16). The -0.5 aligns the structure's tile-centre
    // coords with the world water sprite's pixel grid (anchored at -0.5) — without it the bucket
    // stops half a tile high.
    public float WaterSurfaceLocalY() {
        float fraction = Capacity > 0 ? (float)storedWater / Capacity : 0f;
        int rows     = (Shape.ny - 1) * 16 - FloorLipPx;
        int fillRows = Mathf.RoundToInt(fraction * Mathf.Max(0, rows));
        return (FloorLipPx + fillRows) / 16f - 0.5f;
    }

    // Where the bucket bottoms out: dipped into the water, but never below its rest on the shaft
    // floor (so a near-empty well still drops the bucket all the way to the bottom).
    public float BucketDrawTargetY() {
        float floorRest = FloorLipPx / 16f - 0.5f + BucketHalfTiles;   // bucket base on the floor lip
        return Mathf.Max(floorRest, WaterSurfaceLocalY() - BucketSubmergeDip);
    }

    // Begin a draw for `a`. Returns false if already serving someone (shouldn't happen with the
    // capacity-1 workstation reservation). CompleteDraw finishes the objective when the bucket is up.
    public bool StartDraw(Animal a, DrawWaterObjective obj) {
        if (IsDrawing) return false;
        _drawAnimal    = a;
        _drawObjective = obj;
        bucketY        = WellheadLocalY;
        drawPhase      = DrawPhase.Lowering;
        return true;
    }

    // Advanced per-FRAME by WellBucket (dt = scaled deltaTime) so the bucket glides smoothly and the
    // raise plays out in full — a per-tick advance finished the raise in one step and hid it instantly.
    public void AdvanceDraw(float dt) {
        switch (drawPhase) {
            case DrawPhase.Idle:
                return;
            case DrawPhase.Lowering: {
                float target = BucketDrawTargetY();
                bucketY = Mathf.MoveTowards(bucketY, target, BucketSpeed * dt);
                if (Mathf.Approximately(bucketY, target)) {
                    // Bucket has reached the water and is picking it up: drop the reservoir level now,
                    // holding the drawn amount for the hauler until the bucket is raised back up.
                    _drawnWater  = Mathf.Min(WaterController.LiangToWaterUnits(DrawLiang), storedWater);
                    storedWater -= _drawnWater;
                    drawPhase    = DrawPhase.Soaking;
                    _soakElapsed = 0f;
                }
                return;
            }
            case DrawPhase.Soaking:
                _soakElapsed += dt;
                if (_soakElapsed >= SoakSeconds) drawPhase = DrawPhase.Raising;
                return;
            case DrawPhase.Raising:
                bucketY = Mathf.MoveTowards(bucketY, WellheadLocalY, BucketSpeed * dt);
                if (Mathf.Approximately(bucketY, WellheadLocalY)) CompleteDraw();
                return;
        }
    }

    void CompleteDraw() {
        // Water already left the reservoir when the bucket reached the bottom (see Lowering→Soaking);
        // now the bucket is up, so hand the held amount to the hauler.
        if (_drawAnimal != null && _drawnWater > 0
                && Db.itemByName.TryGetValue("water", out Item water) && water != null)
            _drawAnimal.Produce(water, WaterController.WaterUnitsToFen(_drawnWater));   // sim units → fen
        var obj = _drawObjective;
        ResetDraw();
        obj?.Complete();   // advances the task to its DropObjective
    }

    // Abort an in-flight draw (well destroyed, or the hauler's task died). Bucket snaps back up;
    // the objective is failed so the hauler re-plans rather than waiting on a draw that won't finish.
    public void AbortDraw() {
        if (!IsDrawing) return;
        storedWater += _drawnWater;   // refund water pulled at the bottom but never handed off
        var obj = _drawObjective;
        ResetDraw();
        obj?.Fail();
    }

    void ResetDraw() {
        drawPhase = DrawPhase.Idle;
        _drawAnimal = null;
        _drawObjective = null;
        _drawnWater = 0;
        bucketY = WellheadLocalY;
    }

    // ── Visual: bucket + rope ────────────────────────────────────────
    GameObject _bucketGO, _ropeGO;

    public override void AttachAnimations() {
        base.AttachAnimations();
        // Rope hangs from the wellhead; the bucket rides its end. Both are children of `go`;
        // WellBucket positions them each frame from bucketY. The rope is a thin solid line, so it's
        // drawn as a tinted 1×1 quad scaled to length (a sprite would tile-gap or stretch) — colour +
        // width sampled from well_rope.png so it matches the art.
        SampleRope(out Color32 ropeCol, out int ropeWidthPx);
        _ropeGO = new GameObject($"struct_{structType.name}_rope");
        _ropeGO.transform.SetParent(go.transform, false);
        var rsr = SpriteMaterialUtil.AddSpriteRenderer(_ropeGO);
        rsr.sprite = WhiteSprite();
        rsr.color  = ropeCol;
        rsr.sortingOrder = sr.sortingOrder + 2;
        LightReceiverUtil.SetSortBucket(rsr);

        _bucketGO = new GameObject($"struct_{structType.name}_bucket");
        _bucketGO.transform.SetParent(go.transform, false);
        var bsr = SpriteMaterialUtil.AddSpriteRenderer(_bucketGO);
        bsr.sprite = Resources.Load<Sprite>("Sprites/Buildings/" + structType.name + "_bucket");
        bsr.sortingOrder = sr.sortingOrder + 3;
        LightReceiverUtil.SetSortBucket(bsr);

        bucketY = WellheadLocalY;   // rest position before any draw
        var wb = _bucketGO.AddComponent<WellBucket>();
        wb.well = this;
        wb.bucketSr = bsr;
        wb.ropeSr = rsr;
        wb.ropeWidthTiles = ropeWidthPx / 16f;
    }

    // 1×1 white sprite at PPU 1 (1 unit = 1 tile) so the rope quad scales cleanly to any length.
    static Sprite _whiteSprite;
    static Sprite WhiteSprite() {
        if (_whiteSprite == null) {
            var tex = new Texture2D(1, 1) { filterMode = FilterMode.Point };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        }
        return _whiteSprite;
    }

    // Sample the rope's solid colour + pixel width from well_rope.png (a thin vertical line) so the
    // drawn quad matches the art. Falls back to a tan 1px line if the texture isn't readable.
    void SampleRope(out Color32 col, out int widthPx) {
        col = new Color32(230, 210, 160, 255);
        widthPx = 1;
        var tex = Resources.Load<Texture2D>("Sprites/Buildings/" + structType.name + "_rope");
        if (tex == null || !tex.isReadable) return;
        var px = tex.GetPixels32();
        int w = tex.width, midY = tex.height / 2;
        int minX = int.MaxValue, maxX = int.MinValue;
        Color32 found = col;
        for (int x = 0; x < w; x++) {
            Color32 c = px[midY * w + x];
            if (c.a >= 128) { if (x < minX) minX = x; if (x > maxX) maxX = x; found = c; }
        }
        if (maxX >= minX) { col = found; widthPx = maxX - minX + 1; }
    }
}
