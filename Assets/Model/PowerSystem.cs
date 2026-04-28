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

    // True when a powered consumer received its full demand on the most recent allocation.
    // Reset at the start of every tick. IsBuildingPowered() reads this.
    readonly HashSet<IPowerConsumer> powered = new();

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

    // ── Topology rebuild ──────────────────────────────────────────────────
    // BFS over shaft tiles to compute connected components, then attach producers
    // and consumers via their ports. After this runs:
    //   shaftNet[s]    = id for every live shaft
    //   producerNet[p] = id (or absent if the producer's port doesn't reach a shaft)
    //   consumerNet[c] = id (or absent — consumer is "disconnected", treated as unpowered)
    //   networks[id]   = populated PowerNetwork
    void RebuildTopology() {
        topologyDirty = false;
        shaftNet.Clear();
        producerNet.Clear();
        consumerNet.Clear();
        storageNet.Clear();
        networks.Clear();

        // Build a tile→shaft index for O(1) neighbour lookup during BFS, then a second
        // pass attaches producers/consumers. Stale shaft references (e.g. structure
        // already destroyed) are pruned from `shafts` here.
        var byTile = new Dictionary<(int, int), Structure>();
        var stale  = new List<Structure>();
        foreach (Structure s in shafts) {
            if (s == null || s.go == null) { stale.Add(s); continue; }
            // Shafts are 1×1 in v1. If we add multi-tile shafts later, register every covered tile.
            byTile[(s.x, s.y)] = s;
        }
        foreach (Structure s in stale) shafts.Remove(s);

        int nextId = 0;
        foreach (Structure root in shafts) {
            if (shaftNet.ContainsKey(root)) continue;
            int id = nextId++;
            var net = new PowerNetwork { id = id };
            networks[id] = net;

            // BFS: walk shaft-to-shaft using axis-compatibility rules.
            var queue = new Queue<Structure>();
            queue.Enqueue(root);
            shaftNet[root] = id;
            net.shafts.Add(root);
            while (queue.Count > 0) {
                Structure cur = queue.Dequeue();
                Axis curAxis = AxisOf(cur);
                // Horizontal neighbours — only if both ends carry H (or are turning).
                if (curAxis == Axis.Horizontal || curAxis == Axis.Both) {
                    TryEnqueueNeighbour(cur.x - 1, cur.y, Axis.Horizontal, byTile, queue, net, id);
                    TryEnqueueNeighbour(cur.x + 1, cur.y, Axis.Horizontal, byTile, queue, net, id);
                }
                if (curAxis == Axis.Vertical || curAxis == Axis.Both) {
                    TryEnqueueNeighbour(cur.x, cur.y - 1, Axis.Vertical, byTile, queue, net, id);
                    TryEnqueueNeighbour(cur.x, cur.y + 1, Axis.Vertical, byTile, queue, net, id);
                }
            }
        }

        // Attach producers and consumers via their declared ports.
        foreach (IPowerProducer p in producers) {
            if (p?.Structure == null) continue;
            int? id = FindAttachedNetwork(p.Structure, p.Ports, byTile);
            if (id == null) continue;
            producerNet[p] = id.Value;
            networks[id.Value].producers.Add(p);
        }
        foreach (IPowerConsumer c in consumers) {
            if (c?.Structure == null) continue;
            int? id = FindAttachedNetwork(c.Structure, c.Ports, byTile);
            if (id == null) continue;
            consumerNet[c] = id.Value;
            networks[id.Value].consumers.Add(c);
        }
        foreach (IPowerStorage st in storage) {
            if (st?.Structure == null) continue;
            int? id = FindAttachedNetwork(st.Structure, st.Ports, byTile);
            if (id == null) continue;
            storageNet[st] = id.Value;
            networks[id.Value].storage.Add(st);
        }
    }

    void TryEnqueueNeighbour(int nx, int ny, Axis required,
                             Dictionary<(int, int), Structure> byTile,
                             Queue<Structure> queue, PowerNetwork net, int id) {
        if (!byTile.TryGetValue((nx, ny), out Structure neighbour)) return;
        if (shaftNet.ContainsKey(neighbour)) return;
        Axis na = AxisOf(neighbour);
        if (na != required && na != Axis.Both) return;
        shaftNet[neighbour] = id;
        net.shafts.Add(neighbour);
        queue.Enqueue(neighbour);
    }

    int? FindAttachedNetwork(Structure anchor, IEnumerable<PowerPort> ports,
                             Dictionary<(int, int), Structure> byTile) {
        if (ports == null) return null;
        bool mirrored = anchor.mirrored;
        int nx = anchor.structType.nx;
        foreach (PowerPort port in ports) {
            // Mirroring flips X offsets the same way Structure.workTile does.
            int dx = mirrored ? (nx - 1 - port.dx) : port.dx;
            int tx = anchor.x + dx;
            int ty = anchor.y + port.dy;
            if (!byTile.TryGetValue((tx, ty), out Structure shaft)) continue;
            Axis sa = AxisOf(shaft);
            // Port axis must match the shaft's axis. "Both" on either side is a free pass.
            if (port.axis == Axis.Both || sa == Axis.Both || sa == port.axis) {
                if (shaftNet.TryGetValue(shaft, out int id)) return id;
            }
        }
        return null;
    }

    static Axis AxisOf(Structure shaft) {
        // PowerShaft holds the kind. Cast is safe because only PowerShaft instances
        // are added to `shafts`.
        return (shaft as PowerShaft)?.axis ?? Axis.Both;
    }

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
            foreach (IPowerConsumer c in net.consumers) {
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

            // Apply storage changes — discharge proportional to each unit's MaxDischarge.
            if (storageDrawn > 1e-6f && storageAvailable > 1e-6f) {
                foreach (IPowerStorage st in net.storage) {
                    float share = Mathf.Max(0f, st.MaxDischarge) / storageAvailable;
                    st.ApplyDelta(-storageDrawn * share);
                }
            }

            // Surplus (after non-storage allocation) charges storage in registration order.
            if (supplyRemaining > 1e-6f) {
                foreach (IPowerStorage st in net.storage) {
                    if (supplyRemaining <= 0f) break;
                    float intake = Mathf.Min(supplyRemaining, Mathf.Max(0f, st.MaxIntake));
                    if (intake <= 0f) continue;
                    st.ApplyDelta(intake);
                    supplyRemaining -= intake;
                    net.flowing = true; // storage absorbing surplus counts as power flowing
                }
            }

            net.leftover = supplyRemaining;
        }
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
