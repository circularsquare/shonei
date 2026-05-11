using System;
using System.Collections.Generic;
using UnityEngine;

// Mechanical-power simulation. A scalar "power" flows through connected shaft tiles
// from registered producers to registered consumers, modelled as connected components.
//
// ── Lifecycle ─────────────────────────────────────────────────────────
//   Create()  — called from World.Awake() (alongside MaintenanceSystem).
//   Tick()    — called from World.Update() on the 1-second cadence.
//
// ── Topology ──────────────────────────────────────────────────────────
//   Shafts (PowerShaft) are transmission tiles. Two shafts are in the same network
//   if they're orthogonally adjacent and their axes are compatible:
//     - H ↔ H along x (left/right neighbour)
//     - V ↔ V along y (up/down neighbour)
//     - turning shaft (HV) connects on both axes
//   Producers and consumers attach to a network via PowerPort(s) — each port specifies
//   a relative tile offset from the building anchor and an axis. For the port to count,
//   the shaft tile at that offset must exist and have a compatible axis.
//
// ── Allocation ────────────────────────────────────────────────────────
//   For each network per tick: supply = sum of active producer outputs. Walk consumers
//   in registration order; each gets full demand or none. (Future work: fractional.)
//
// ── Topology rebuild ──────────────────────────────────────────────────
//   PowerShaft.OnPlaced/Destroy and any producer/consumer add/remove call MarkDirty().
//   Tick() rebuilds before allocating if dirty. Cheap (O(power-relevant tiles)).
public class PowerSystem {
    public static PowerSystem instance { get; private set; }

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }

    // Axis a shaft tile carries, or that a port couples to.
    //   Horizontal — connects on x; Vertical — connects on y; Both — turning shaft / either-axis port.
    public enum Axis { Horizontal, Vertical, Both }

    // A connection point on a producer/consumer building. dx/dy are tile offsets
    // from the building anchor (NOT mirrored — subclasses decide whether to flip
    // their own ports based on `mirrored`). axis is the axis that must be present
    // on the shaft tile at (anchor.x+dx, anchor.y+dy). Use Both for "either axis works".
    public struct PowerPort {
        public int dx, dy;
        public Axis axis;
        public PowerPort(int dx, int dy, Axis axis) { this.dx = dx; this.dy = dy; this.axis = axis; }
    }

    // Active rotational machinery (mouse wheel, windmill, future flywheel).
    // Output is sampled each tick — return 0 when not currently producing (no operator,
    // no wind, etc.) instead of unregistering. Ports describe shaft attachment points.
    public interface IPowerProducer {
        Structure Structure { get; }                // anchor structure (back-ref for IsBuildingPowered queries)
        float CurrentOutput { get; }                // 0 when idle
        IEnumerable<PowerPort> Ports { get; }
    }

    // Power consumers (currently: pump/press boost, future: elevator). Demand is the
    // amount of power the consumer wants when it's actively trying to use power.
    // For workstations using powerBoost, demand can be a constant — IsBuildingPowered
    // is the only thing that matters at the gate site.
    public interface IPowerConsumer {
        Structure Structure { get; }
        float CurrentDemand { get; }
        IEnumerable<PowerPort> Ports { get; }
    }

    // Power storage (flywheel, future battery banks). Conceptually both a producer and
    // a consumer, but with state — discharges to cover network deficits, absorbs surplus,
    // and naturally bleeds energy via decay. PowerSystem orchestrates charge/discharge
    // each tick rather than letting the storage drive its own producer/consumer values
    // (which would create a circular dependency on net state).
    public interface IPowerStorage {
        Structure Structure { get; }
        IEnumerable<PowerPort> Ports { get; }
        // Maximum power this unit can output this tick (limited by current charge AND a
        // per-tick output rate cap).
        float MaxDischarge { get; }
        // Maximum power this unit can absorb this tick (limited by remaining capacity AND
        // a per-tick intake rate cap).
        float MaxIntake { get; }
        // Current charge as a fraction of capacity in [0, 1]. Used by Allocate to equalize
        // fill levels across storages on the same network — surplus charges the *emptiest*
        // storage first, deficits drain from the *fullest*. Same denominator (fraction, not
        // absolute charge) so a 50-unit flywheel and a hypothetical 200-unit battery bank
        // are compared fairly.
        float ChargeFraction { get; }
        // Apply the net change PowerSystem decided this tick. Positive = charged, negative
        // = discharged. Caller has already clamped to [-MaxDischarge, +MaxIntake].
        void ApplyDelta(float delta);
        // Per-tick decay step, called once per PowerSystem.Tick before Allocate. Lets the
        // unit own its decay rule (exponential, linear, capped, etc.).
        void StorageTick();
    }

    // ── State ─────────────────────────────────────────────────────────────
    // All registered shafts, producers, consumers. Sets so duplicate-registration
    // (e.g. accidental double-OnPlaced) is harmless. Iteration order doesn't matter
    // for shafts/producers; consumer registration order *does* matter for allocation
    // — the registration list mirrors that ordering.
    readonly HashSet<Structure>      shafts       = new();
    readonly HashSet<IPowerProducer> producers    = new();
    readonly List<IPowerConsumer>    consumers    = new(); // ordered: first-registered served first
    readonly HashSet<IPowerStorage>  storage      = new();

    // Network bookkeeping rebuilt by RebuildTopology(). Maps each shaft / producer /
    // consumer / storage unit to a network id, and each network id to its members + allocation state.
    readonly Dictionary<Structure, int>      shaftNet     = new();
    readonly Dictionary<IPowerProducer, int> producerNet  = new();
    readonly Dictionary<IPowerConsumer, int> consumerNet  = new();
    readonly Dictionary<IPowerStorage, int>  storageNet   = new();
    readonly Dictionary<int, PowerNetwork>   networks     = new();

    // Tile→shaft lookup, kept in sync with `shaftNet`. Used by the BFS in RebuildTopology
    // and by external HasCompatibleShaftAt queries (port-stub visuals, etc).
    readonly Dictionary<(int, int), Structure> byTile = new();

    // World tiles where a 0-gap connection (Phase 3 of RebuildTopology) has fired,
    // along with the axis the connection runs along. Read by HasCompatibleShaftAt so
    // PortStubVisuals renders stubs at both ends of a 0-gap link — building sprites
    // don't fully cover their tiles, so the stub sprites show through the gaps and
    // visually convey "axle running between the two buildings". Cleared and rebuilt
    // each topology pass.
    readonly Dictionary<(int, int), Axis> connectedPortTiles = new();

    // Fired at the end of RebuildTopology(). Subscribers refresh any visualization
    // derived from connectivity (port stubs today; debug overlays in future). The
    // topology dirty flag is already cleared when this fires, so handlers calling
    // HasCompatibleShaftAt won't trigger a recursive rebuild.
    public event Action onTopologyRebuilt;

    // True when a powered consumer received its full demand on the most recent allocation.
    // Reset at the start of every tick. IsBuildingPowered() reads this.
    readonly HashSet<IPowerConsumer> powered = new();

    // Monotonic per-Allocate counter used to rotate which consumer is served first on
    // each network. Without rotation, registration order would mean the first-placed
    // pump always wins on a starved network and later pumps never run — placement order
    // shouldn't determine fairness. Walking starts at `allocationCounter % consumers.Count`
    // and wraps, so over N ticks every consumer takes a turn at the front of the queue.
    // The counter is global rather than per-network because PowerNetwork instances are
    // rebuilt on every topology change; a global counter survives rebuilds with no state
    // bookkeeping.
    int allocationCounter;

    bool topologyDirty = true;

    // A single connected component. Members are populated during RebuildTopology;
    // supply / leftover are computed in Allocate.
    public class PowerNetwork {
        public int id;
        public readonly List<Structure>      shafts    = new();
        public readonly List<IPowerProducer> producers = new();
        public readonly List<IPowerConsumer> consumers = new();
        public readonly List<IPowerStorage>  storage   = new();
        public float supply;        // current tick's total producer output (before storage)
        public float leftover;      // raw supply minus already-allocated demand (before storage charge)
        // True iff any consumer was powered or any storage absorbed surplus this tick. Read by
        // PowerShaft animation to decide whether shaft tiles should spin. Updated each Allocate.
        public bool flowing;
    }

    public static PowerSystem Create() {
        instance = new PowerSystem();
        return instance;
    }

    // ── Registration API ──────────────────────────────────────────────────
    // Called by PowerShaft.OnPlaced. Idempotent. Marks topology dirty.
    public void RegisterShaft(Structure s) {
        if (s == null) return;
        if (shafts.Add(s)) topologyDirty = true;
    }
    public void UnregisterShaft(Structure s) {
        if (s == null) return;
        if (shafts.Remove(s)) topologyDirty = true;
        shaftNet.Remove(s);
    }

    public void RegisterProducer(IPowerProducer p) {
        if (p == null) return;
        if (producers.Add(p)) topologyDirty = true;
    }
    public void UnregisterProducer(IPowerProducer p) {
        if (p == null) return;
        if (producers.Remove(p)) topologyDirty = true;
        producerNet.Remove(p);
    }

    public void RegisterConsumer(IPowerConsumer c) {
        if (c == null || consumers.Contains(c)) return;
        consumers.Add(c);
        topologyDirty = true;
    }
    public void UnregisterConsumer(IPowerConsumer c) {
        if (c == null) return;
        if (consumers.Remove(c)) topologyDirty = true;
        consumerNet.Remove(c);
        powered.Remove(c);
    }

    public void RegisterStorage(IPowerStorage s) {
        if (s == null) return;
        if (storage.Add(s)) topologyDirty = true;
    }
    public void UnregisterStorage(IPowerStorage s) {
        if (s == null) return;
        if (storage.Remove(s)) topologyDirty = true;
        storageNet.Remove(s);
    }

    // Single hook called from Structure.Destroy() of any power participant. Cheap to
    // call on non-participants — the Set/List Remove returns false and is a no-op.
    // Mirrors MaintenanceSystem.ForgetStructure.
    public void ForgetStructure(Structure s) {
        if (s == null) return;
        if (shafts.Remove(s))     { shaftNet.Remove(s);     topologyDirty = true; }
        if (s is IPowerProducer p && producers.Remove(p)) { producerNet.Remove(p); topologyDirty = true; }
        if (s is IPowerConsumer c) {
            if (consumers.Remove(c)) {
                consumerNet.Remove(c);
                powered.Remove(c);
                topologyDirty = true;
            }
        }
        if (s is IPowerStorage st && storage.Remove(st)) {
            storageNet.Remove(st);
            topologyDirty = true;
        }
    }

    // Forces a topology rebuild on the next tick. Use when a port-relevant world
    // condition changes that the registration hooks don't directly notice (rare).
    public void MarkDirty() { topologyDirty = true; }

    // ── Per-tick entry point ──────────────────────────────────────────────
    public void Tick() {
        if (topologyDirty) RebuildTopology();
        // Decay first so a storage unit on a dead network drifts toward zero. Each unit
        // owns its own decay rule (Flywheel.StorageTick → exponential `charge *= factor`).
        foreach (IPowerStorage st in storage) st.StorageTick();
        Allocate();
    }

    // True iff the shaft's network had any power flow on the most recent Allocate() —
    // i.e. at least one consumer was powered, or storage absorbed surplus. Read by
    // PowerShaft's FrameAnimator to decide whether shaft tiles should spin.
    public bool IsShaftActivelyTransmitting(Structure shaft) {
        if (shaft == null) return false;
        if (!shaftNet.TryGetValue(shaft, out int id)) return false;
        return networks.TryGetValue(id, out PowerNetwork n) && n.flowing;
    }

    // True iff the most recent Allocate() decided this consumer's network had enough
    // supply to fully satisfy it. Read by AnimalStateManager (powerBoost gate).
    public bool IsBuildingPowered(Building b) {
        if (b == null) return false;
        foreach (IPowerConsumer c in consumers)
            if (c.Structure == b) return powered.Contains(c);
        return false;
    }

    // Diagnostic accessor for InfoPanel / audit. Returns null if the building isn't
    // registered as a power participant. Network id is stable only between rebuilds.
    public int? GetNetworkId(Structure s) {
        if (s == null) return null;
        if (s is IPowerProducer p && producerNet.TryGetValue(p, out int pi)) return pi;
        if (s is IPowerConsumer c && consumerNet.TryGetValue(c, out int ci)) return ci;
        if (s is IPowerStorage  st && storageNet.TryGetValue(st, out int sti)) return sti;
        // Wrapped consumers (pump/press): the Building itself isn't IPowerConsumer — its
        // BuildingPowerConsumer wrapper is what's registered. Reach through to the wrapper.
        if (s is Building b && b.powerConsumer != null
                && consumerNet.TryGetValue(b.powerConsumer, out int wi)) return wi;
        if (shaftNet.TryGetValue(s, out int sni)) return sni;
        return null;
    }

    public PowerNetwork GetNetwork(int id) =>
        networks.TryGetValue(id, out PowerNetwork n) ? n : null;

    // True iff (tx, ty) is a tile where a port stub should render — either:
    //   1. A real shaft exists at the tile with axis compatible with `required`, OR
    //   2. The tile is one end of a 0-gap connection (Phase 3 of topology rebuild)
    //      with axis compatible with `required`.
    //
    // "Both" on either side is a free pass. Used by port-stub visuals to decide
    // whether a building's port stub should show. Case 2 ensures stubs render on
    // both ends of a 0-gap connection — building sprites don't fully cover their
    // tiles, so the stubs show through the gaps to visually convey the axle.
    //
    // Lazily rebuilds topology if dirty so callers see up-to-date state without
    // having to wait for the next Tick.
    public bool HasCompatibleShaftAt(int tx, int ty, Axis required) {
        if (topologyDirty) RebuildTopology();
        if (byTile.TryGetValue((tx, ty), out Structure shaft)) {
            Axis sa = AxisOf(shaft);
            return required == Axis.Both || sa == Axis.Both || sa == required;
        }
        if (connectedPortTiles.TryGetValue((tx, ty), out Axis ca)) {
            return required == Axis.Both || ca == Axis.Both || ca == required;
        }
        return false;
    }

    // ── Topology rebuild ──────────────────────────────────────────────────
    // Three phases, glued together by union-find over a sparse network-id space:
    //
    //   1. BFS over real shafts → each shaft gets a raw network id.
    //   2. Walk participants. For each, find every port that lands on a real shaft and
    //      union those networks (multi-port bridging — a building with shafts on both
    //      sides becomes one network, not two). While iterating, also index every port
    //      by row (H/Both) and column (V/Both) for Phase 3.
    //   3. 0-gap adjacency rule. When two power buildings have touching footprints and
    //      their ports point at each other across the shared edge, they auto-connect —
    //      the player can't drop a `power shaft` between buildings that have no space
    //      between them, so the rule fills in for that case. Detected via the row/col
    //      indexes: pairs of ports from distinct anchors with axis-tile distance == 1.
    //      (Distance 0 = 1-tile gap with port-coincidence — explicitly NOT auto-connect;
    //      the player has space there and is expected to wire it manually.)
    //
    // After all unioning, raw ids are resolved to UF roots and the canonical
    // PowerNetwork dict is populated.
    void RebuildTopology() {
        topologyDirty = false;
        shaftNet.Clear();
        producerNet.Clear();
        consumerNet.Clear();
        storageNet.Clear();
        networks.Clear();
        byTile.Clear();
        connectedPortTiles.Clear();

        // Build a tile→shaft index for O(1) neighbour lookup during BFS, then later
        // passes attach participants. Stale shaft references (e.g. structure already
        // destroyed) are pruned from `shafts` here. byTile persists as a field so
        // external queries (HasCompatibleShaftAt) work outside the BFS scope.
        var stale = new List<Structure>();
        foreach (Structure s in shafts) {
            if (s == null || s.go == null) { stale.Add(s); continue; }
            // Shafts are 1×1 in v1. If we add multi-tile shafts later, register every covered tile.
            byTile[(s.x, s.y)] = s;
        }
        foreach (Structure s in stale) shafts.Remove(s);

        // ── Union-find scratch (used by Phase 2 + 3) ──────────────────
        // Maps raw network id → parent. Grows as fresh ids get minted in Phase 3.
        var ufParent = new Dictionary<int, int>();
        int Find(int x) {
            if (!ufParent.TryGetValue(x, out int p)) { ufParent[x] = x; return x; }
            int r = x;
            while (ufParent[r] != r) r = ufParent[r];
            // Path compression — collapse the chain on the way out.
            while (ufParent[x] != r) { int next = ufParent[x]; ufParent[x] = r; x = next; }
            return r;
        }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) ufParent[ra] = rb; }

        // ── Phase 1: BFS over real shafts ─────────────────────────────
        int nextId = 0;
        foreach (Structure root in shafts) {
            if (shaftNet.ContainsKey(root)) continue;
            int id = nextId++;
            ufParent[id] = id;
            shaftNet[root] = id;

            var queue = new Queue<Structure>();
            queue.Enqueue(root);
            while (queue.Count > 0) {
                Structure cur = queue.Dequeue();
                Axis curAxis = AxisOf(cur);
                if (curAxis == Axis.Horizontal || curAxis == Axis.Both) {
                    TryEnqueueNeighbour(cur.x - 1, cur.y, Axis.Horizontal, queue, id);
                    TryEnqueueNeighbour(cur.x + 1, cur.y, Axis.Horizontal, queue, id);
                }
                if (curAxis == Axis.Vertical || curAxis == Axis.Both) {
                    TryEnqueueNeighbour(cur.x, cur.y - 1, Axis.Vertical, queue, id);
                    TryEnqueueNeighbour(cur.x, cur.y + 1, Axis.Vertical, queue, id);
                }
            }
        }

        // ── Phase 2: attach participants + multi-port bridging ────────
        // For each participant, walk ports. Real-shaft hits union into the multi-port
        // bridge (so a building with shafts on multiple sides becomes one network).
        // Every port — real-shaft hit or not — is also indexed by row (H/Both) and
        // column (V/Both) for Phase 3's 0-gap rule.
        var producerRaw = new Dictionary<IPowerProducer, int>();
        var consumerRaw = new Dictionary<IPowerConsumer, int>();
        var storageRaw  = new Dictionary<IPowerStorage, int>();
        var anchorRaw   = new Dictionary<Structure, int>();
        // Port records carry a direction sign so Phase 3 can require pairs to actually
        // face each other. dir = -1 means the port-target tile is OUTSIDE the building
        // on the LEFT (for row index) or BELOW (for col index); dir = +1 means RIGHT or
        // ABOVE. Without this, two ports both pointing right (e.g. a pump's right port
        // and a press's right port a tile further along) would spuriously pair as
        // "distance 1, different anchors", even though they don't share an axle.
        var portsByRow = new Dictionary<int, List<(int x, Structure anchor, Axis axis, int dir)>>();
        var portsByCol = new Dictionary<int, List<(int y, Structure anchor, Axis axis, int dir)>>();

        // Walk one participant's ports, union real-shaft hits, populate row/col indexes.
        // Returns the participant's chosen raw id, or null if no port hit a real shaft.
        int? Attach(Structure anchor, IEnumerable<PowerPort> ports) {
            if (anchor == null || ports == null) return null;
            bool mirrored = anchor.mirrored;
            int nx = anchor.structType.nx;
            int? pickedId = null;
            foreach (PowerPort port in ports) {
                // Mirroring flips X offsets the same way Structure.workTile does.
                int dx = mirrored ? (nx - 1 - port.dx) : port.dx;
                int tx = anchor.x + dx;
                int ty = anchor.y + port.dy;

                // Index for Phase 3 (0-gap rule). H/Both ports go in the row index; V/Both
                // in the column index. A Both port shows up in both — it can pair via
                // either axis. Direction signs are recorded per index: for the row index,
                // dir is based on dx relative to the building's footprint (negative dx = LEFT,
                // dx >= nx = RIGHT, otherwise inside-footprint = 0 and won't pair); for the
                // column index, the same rule on dy / ny.
                int ny = Mathf.Max(1, anchor.structType.ny);
                if (port.axis == Axis.Horizontal || port.axis == Axis.Both) {
                    if (!portsByRow.TryGetValue(ty, out var rl)) {
                        rl = new List<(int, Structure, Axis, int)>();
                        portsByRow[ty] = rl;
                    }
                    int hDir = (dx < 0) ? -1 : (dx >= nx ? +1 : 0);
                    rl.Add((tx, anchor, port.axis, hDir));
                }
                if (port.axis == Axis.Vertical || port.axis == Axis.Both) {
                    if (!portsByCol.TryGetValue(tx, out var cl)) {
                        cl = new List<(int, Structure, Axis, int)>();
                        portsByCol[tx] = cl;
                    }
                    int vDir = (port.dy < 0) ? -1 : (port.dy >= ny ? +1 : 0);
                    cl.Add((ty, anchor, port.axis, vDir));
                }

                // Real-shaft attachment + multi-port bridging.
                if (byTile.TryGetValue((tx, ty), out Structure shaft)) {
                    Axis sa = AxisOf(shaft);
                    if (port.axis == Axis.Both || sa == Axis.Both || sa == port.axis) {
                        if (shaftNet.TryGetValue(shaft, out int id)) {
                            if (pickedId == null) pickedId = id;
                            else Union(pickedId.Value, id);  // multi-port bridge
                        }
                    }
                }
            }
            return pickedId;
        }

        // Records a participant's raw id, unioning with any prior id assigned to the
        // same anchor (rare — only happens for multi-role participants).
        void RecordAnchor(Structure anchor, int id) {
            if (anchorRaw.TryGetValue(anchor, out int existing)) Union(existing, id);
            else anchorRaw[anchor] = id;
        }

        foreach (IPowerProducer p in producers) {
            int? id = Attach(p?.Structure, p?.Ports);
            if (id != null) { producerRaw[p] = id.Value; RecordAnchor(p.Structure, id.Value); }
        }
        foreach (IPowerConsumer c in consumers) {
            int? id = Attach(c?.Structure, c?.Ports);
            if (id != null) { consumerRaw[c] = id.Value; RecordAnchor(c.Structure, id.Value); }
        }
        foreach (IPowerStorage st in storage) {
            int? id = Attach(st?.Structure, st?.Ports);
            if (id != null) { storageRaw[st] = id.Value; RecordAnchor(st.Structure, id.Value); }
        }

        // ── Phase 3: 0-gap adjacency rule ─────────────────────────────
        // Two power buildings with touching footprints, with ports facing each other
        // across the shared edge, auto-connect. Detected via port-target tiles being
        // adjacent (distance EXACTLY 1) along the port's axis. Distance 0 (same tile,
        // 1-gap with port-coincidence) is intentionally NOT auto-connected — the player
        // has space there and is expected to wire it with a real shaft.
        void EnsureAnchorId(Structure anc) {
            if (anc == null || anchorRaw.ContainsKey(anc)) return;
            int id = nextId++;
            ufParent[id] = id;
            anchorRaw[anc] = id;
            foreach (IPowerProducer pp in producers)
                if (pp?.Structure == anc && !producerRaw.ContainsKey(pp)) producerRaw[pp] = id;
            foreach (IPowerConsumer cc in consumers)
                if (cc?.Structure == anc && !consumerRaw.ContainsKey(cc)) consumerRaw[cc] = id;
            foreach (IPowerStorage ss in storage)
                if (ss?.Structure == anc && !storageRaw.ContainsKey(ss))  storageRaw[ss]  = id;
        }

        void ConnectAnchors(Structure a, Structure b) {
            if (a == null || b == null || a == b) return;
            EnsureAnchorId(a);
            EnsureAnchorId(b);
            Union(anchorRaw[a], anchorRaw[b]);
        }

        // Marks both ends of a 0-gap link as port-stub-renderable, with the connection
        // axis. If a tile already has a marker on a different axis (rare — would mean
        // the same tile is both ends of an H link and a V link), upgrade to Both so
        // either-axis port stubs at that tile both render.
        void MarkConnectedTile(int x, int y, Axis a) {
            if (connectedPortTiles.TryGetValue((x, y), out Axis existing)) {
                if (existing != a) connectedPortTiles[(x, y)] = Axis.Both;
            } else {
                connectedPortTiles[(x, y)] = a;
            }
        }

        // Walk each row/col, sort by the axis coord, and union pairs at distance == 1
        // whose direction signs are opposite (lower coord = LEFT/DOWN-pointing port from
        // the building on the higher side; higher coord = RIGHT/UP-pointing port from the
        // building on the lower side). Same-direction pairs (e.g. two right-pointing ports
        // a tile apart) don't actually face each other and shouldn't union.
        // O(P log P) over total port count P — negligible.
        foreach (var kv in portsByRow) {
            int rowY = kv.Key;
            var list = kv.Value;
            list.Sort((a, b) => a.x.CompareTo(b.x));
            for (int i = 0; i < list.Count; i++) {
                for (int j = i + 1; j < list.Count; j++) {
                    int diff = list[j].x - list[i].x;
                    if (diff > 1) break;
                    if (diff == 0) continue;                            // 1-gap — leave to player
                    if (list[i].anchor == list[j].anchor) continue;
                    if (list[i].dir >= 0 || list[j].dir <= 0) continue; // not facing each other
                    ConnectAnchors(list[i].anchor, list[j].anchor);
                    MarkConnectedTile(list[i].x, rowY, Axis.Horizontal);
                    MarkConnectedTile(list[j].x, rowY, Axis.Horizontal);
                }
            }
        }
        foreach (var kv in portsByCol) {
            int colX = kv.Key;
            var list = kv.Value;
            list.Sort((a, b) => a.y.CompareTo(b.y));
            for (int i = 0; i < list.Count; i++) {
                for (int j = i + 1; j < list.Count; j++) {
                    int diff = list[j].y - list[i].y;
                    if (diff > 1) break;
                    if (diff == 0) continue;                            // 1-gap — leave to player
                    if (list[i].anchor == list[j].anchor) continue;
                    if (list[i].dir >= 0 || list[j].dir <= 0) continue; // not facing each other
                    ConnectAnchors(list[i].anchor, list[j].anchor);
                    MarkConnectedTile(colX, list[i].y, Axis.Vertical);
                    MarkConnectedTile(colX, list[j].y, Axis.Vertical);
                }
            }
        }

        // ── Phase 4: resolve raw ids to UF roots, populate networks dict ──
        // After all unioning, sparse raw ids collapse to canonical roots. Iterate every
        // raw map and bucket members into the canonical PowerNetwork they now belong to.
        PowerNetwork GetNet(int rawId) {
            int root = Find(rawId);
            if (!networks.TryGetValue(root, out PowerNetwork net)) {
                net = new PowerNetwork { id = root };
                networks[root] = net;
            }
            return net;
        }

        var shaftEntries = new List<KeyValuePair<Structure, int>>(shaftNet);
        foreach (var kv in shaftEntries) {
            PowerNetwork net = GetNet(kv.Value);
            shaftNet[kv.Key] = net.id;
            net.shafts.Add(kv.Key);
        }
        foreach (var kv in producerRaw) {
            PowerNetwork net = GetNet(kv.Value);
            producerNet[kv.Key] = net.id;
            net.producers.Add(kv.Key);
        }
        foreach (var kv in consumerRaw) {
            PowerNetwork net = GetNet(kv.Value);
            consumerNet[kv.Key] = net.id;
            net.consumers.Add(kv.Key);
        }
        foreach (var kv in storageRaw) {
            PowerNetwork net = GetNet(kv.Value);
            storageNet[kv.Key] = net.id;
            net.storage.Add(kv.Key);
        }

        // Notify visual subscribers that connectivity may have changed. Fired AFTER
        // topologyDirty is cleared (above) so handlers calling HasCompatibleShaftAt
        // see the freshly-rebuilt state without triggering another rebuild.
        onTopologyRebuilt?.Invoke();
    }

    void TryEnqueueNeighbour(int nx, int ny, Axis required, Queue<Structure> queue, int id) {
        if (!byTile.TryGetValue((nx, ny), out Structure neighbour)) return;
        if (shaftNet.ContainsKey(neighbour)) return;
        Axis na = AxisOf(neighbour);
        if (na != required && na != Axis.Both) return;
        shaftNet[neighbour] = id;
        queue.Enqueue(neighbour);
    }

    static Axis AxisOf(Structure shaft) {
        // PowerShaft holds the kind. Cast is safe because only PowerShaft instances
        // are added to `shafts`.
        return (shaft as PowerShaft)?.axis ?? Axis.Both;
    }

    // Sort comparators for storage equalization. Fullest-first (descending) for discharge,
    // emptiest-first (ascending) for charge — both work to drive ChargeFraction toward equal
    // across the network's storage units.
    static readonly Comparison<IPowerStorage> StorageByChargeDesc =
        (a, b) => b.ChargeFraction.CompareTo(a.ChargeFraction);
    static readonly Comparison<IPowerStorage> StorageByChargeAsc =
        (a, b) => a.ChargeFraction.CompareTo(b.ChargeFraction);

    // ── Allocation ────────────────────────────────────────────────────────
    // Order of operations per network per tick:
    //   1. Sum producer outputs (raw supply, excludes storage).
    //   2. Compute the storage units' aggregate discharge headroom for this tick.
    //   3. Walk consumers in registration order; each draws from raw supply first, then
    //      from storage if needed. Allocation is binary (full demand or none).
    //   4. If any storage was drawn from, distribute the discharge across units in proportion
    //      to their MaxDischarge so they all bleed evenly.
    //   5. Any raw-supply leftover (after consumers) charges storage units in registration
    //      order until the surplus or their MaxIntake is exhausted.
    // Decay runs in Tick() before Allocate so a freshly placed storage with no power on the
    // network drifts toward zero rather than staying full.
    void Allocate() {
        powered.Clear();
        foreach (var kv in networks) {
            PowerNetwork net = kv.Value;
            net.flowing = false;
            float supply = 0f;
            foreach (IPowerProducer p in net.producers) supply += Mathf.Max(0f, p.CurrentOutput);
            net.supply = supply;

            float storageAvailable = 0f;
            foreach (IPowerStorage st in net.storage) storageAvailable += Mathf.Max(0f, st.MaxDischarge);

            float supplyRemaining = supply;
            float storageDrawn    = 0f;
            // Rotate the starting index per-tick so first-placed consumers don't always win
            // on a starved network. Order within the walk still determines who runs and who
            // doesn't (binary fulfillment), but the *starting point* shifts each tick, so
            // every consumer takes a turn at the front of the queue.
            int n = net.consumers.Count;
            int startIdx = n > 0 ? ((allocationCounter % n) + n) % n : 0;
            for (int k = 0; k < n; k++) {
                IPowerConsumer c = net.consumers[(startIdx + k) % n];
                float need = Mathf.Max(0f, c.CurrentDemand);
                if (need <= 0f) continue;
                if (supplyRemaining + 1e-4f >= need) {
                    supplyRemaining -= need;
                    powered.Add(c);
                    net.flowing = true;
                } else if (supplyRemaining + (storageAvailable - storageDrawn) + 1e-4f >= need) {
                    storageDrawn += need - supplyRemaining;
                    supplyRemaining = 0f;
                    powered.Add(c);
                    net.flowing = true;
                }
            }

            // Apply storage discharge. Greedy from the fullest storage first, capped by each
            // unit's MaxDischarge — equalizes charge fractions across the network's storages
            // over time. Cheap: storage counts per network are tiny, so the per-tick sort is
            // negligible and beats maintaining a sorted structure incrementally.
            if (storageDrawn > 1e-6f && net.storage.Count > 0) {
                net.storage.Sort(StorageByChargeDesc);
                float remaining = storageDrawn;
                foreach (IPowerStorage st in net.storage) {
                    if (remaining <= 1e-6f) break;
                    float take = Mathf.Min(remaining, Mathf.Max(0f, st.MaxDischarge));
                    if (take <= 0f) continue;
                    st.ApplyDelta(-take);
                    remaining -= take;
                }
            }

            // Surplus (after non-storage allocation) charges storage. Greedy to the *emptiest*
            // first — same equalization logic in reverse. An emptier flywheel pulls from the
            // network until it matches the next-emptiest level, then they fill together.
            if (supplyRemaining > 1e-6f && net.storage.Count > 0) {
                net.storage.Sort(StorageByChargeAsc);
                foreach (IPowerStorage st in net.storage) {
                    if (supplyRemaining <= 1e-6f) break;
                    float intake = Mathf.Min(supplyRemaining, Mathf.Max(0f, st.MaxIntake));
                    if (intake <= 0f) continue;
                    st.ApplyDelta(intake);
                    supplyRemaining -= intake;
                    net.flowing = true; // storage absorbing surplus counts as power flowing
                }
            }

            net.leftover = supplyRemaining;
        }
        // Advance the rotation cursor so next tick's allocation starts one position later.
        // Bumped after the per-network walk so every network sees the same `allocationCounter`
        // value within a tick (each network mods by its own consumer count, so they're free
        // to disagree on the absolute start index).
        allocationCounter++;
    }

    // ── Save/load ─────────────────────────────────────────────────────────
    // Topology is derived from world state — Phase 6 in SaveSystem walks all live
    // structures and re-registers power participants. Mirrors MaintenanceSystem.RebuildFromWorld.
    public void RebuildFromWorld() {
        shafts.Clear();
        producers.Clear();
        consumers.Clear();
        storage.Clear();
        shaftNet.Clear();
        producerNet.Clear();
        consumerNet.Clear();
        storageNet.Clear();
        networks.Clear();
        byTile.Clear();
        connectedPortTiles.Clear();
        powered.Clear();

        List<Structure> structures = StructController.instance?.GetStructures();
        if (structures == null) { topologyDirty = true; return; }
        foreach (Structure s in structures) {
            if (s == null) continue;
            if (s is PowerShaft)        shafts.Add(s);
            if (s is IPowerProducer pp) producers.Add(pp);
            if (s is IPowerConsumer cc) consumers.Add(cc);
            if (s is IPowerStorage  st) storage.Add(st);
            // Wrapped consumers (pump/press with powerBoost > 1) — the wrapper is created
            // in Building.OnPlaced on the gameplay path, but skipped on load (OnPlaced isn't
            // called when restoring saved structures). Materialise the wrapper here.
            if (s is Building b && b.EnsurePowerConsumer())
                consumers.Add(b.powerConsumer);
        }
        topologyDirty = true;
    }
}
