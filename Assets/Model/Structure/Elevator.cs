using System.Collections.Generic;
using UnityEngine;

// Fixed-size rolling buffer of ints. Used by Elevator to track recent wait & trip
// durations for the cost estimator. Keeps the last `capacity` samples; older entries
// roll out as new ones arrive. Average() returns `fallback` when empty so the cost
// estimator has something sensible during cold start.
public class RingBufferInt {
    readonly int[] data;
    int head;
    int count;
    public RingBufferInt(int capacity) { data = new int[Mathf.Max(1, capacity)]; }
    public int Count => count;
    public int Capacity => data.Length;
    public void Add(int v) {
        data[head] = v;
        head = (head + 1) % data.Length;
        if (count < data.Length) count++;
    }
    public float Average(float fallback) {
        if (count == 0) return fallback;
        long sum = 0;
        for (int i = 0; i < count; i++) sum += data[i];
        return (float)sum / count;
    }

    // Save: dump live entries in insertion order (oldest → newest). Length == Count.
    public int[] ToArray() {
        var result = new int[count];
        // Oldest entry is at (head - count) mod capacity; head points at the next write slot.
        int start = (head - count + data.Length) % data.Length;
        for (int i = 0; i < count; i++) result[i] = data[(start + i) % data.Length];
        return result;
    }

    // Load: replace contents with the given samples, treated as oldest → newest. Excess
    // entries beyond capacity drop the oldest. Null/empty input clears the buffer.
    public void LoadFrom(int[] samples) {
        head = 0;
        count = 0;
        if (samples == null) return;
        foreach (int s in samples) Add(s);
    }
}

// Vertical 2-stop elevator. Carries one mouse at a time between stops at y+0 and y+ny-1
// (both standable via HasInternalFloorAt). The graph is given a direct neighbor edge between
// those two tile-nodes — the "transit edge" — and Graph.GetEdgeInfo asks the elevator for
// its current cost. A* sees the elevator as a regular path option whose cost reflects
// expected ride time (Phase 2: just travel time; Phase 3 adds queue / wait estimate).
//
// Riding handoff: when Nav.Move detects the next path step is a transit edge, it calls
// RequestRide and waits at the boarding tile. Elevator.Tick advances the platform per tick;
// while a passenger is loaded, the elevator drives their (x, y) directly. On arrival it
// calls Nav.OnTransitComplete and the animal resumes normal navigation.
//
// Phase 2: capacity 1, FIFO queue, no power gating, no historical wait estimate.
public class Elevator : Building, PowerSystem.IPowerConsumer {
    // Tiles per in-game tick the platform travels. Tuned so a 9-tile ride takes ~6 ticks,
    // which beats stairs (≥1.8 cost per diagonal step) when the queue is empty.
    public const float PlatformSpeed = 1.2f;

    // ── Static registry ──────────────────────────────────────────────
    static readonly List<Elevator> _all = new();

    // Monotonic tick counter shared by all elevators. Incremented once per TickAll(), so
    // wait / trip durations measured against it are stable in-game seconds.    Not persisted —
    // resets on load; in-flight trips lose their duration measurement but the rolling
    // history (which IS persisted in Phase 5) survives.
    static int currentTick = 0;

    // Called from World.Update on the 1-second cadence (alongside PowerSystem.Tick).
    // Snapshot in case a Tick triggers a Destroy that mutates _all.
    public static void TickAll() {
        currentTick++;
        if (_all.Count == 0) return;
        var snapshot = _all.ToArray();
        foreach (var e in snapshot) e.Tick();
    }

    // ── Stops & transit edge ────────────────────────────────────────
    public Node bottomStop;
    public Node topStop;

    // ── Dispatch state ──────────────────────────────────────────────
    // No explicit Loading state — boarding is instant once the platform reaches the
    // boarding floor (StartTrip and MovingToBoardingFloor call BoardPassenger directly).
    // Unloading is the only deliberate 1-tick pause, so a successful trip visibly settles
    // before the platform commits to the next request.
    public enum DispatchState { Idle, MovingToBoardingFloor, Riding, Unloading }
    public DispatchState dispatchState { get; private set; } = DispatchState.Idle;

    // Continuous platform position in tile units, relative to anchor y. 0 = bottom (y+0),
    // ny-1 = top (y+ny-1). Updated by Tick at PlatformSpeed tiles/tick. Setter is public
    // so SaveSystem can restore it; in normal operation only Tick mutates it.
    public float currentY { get; set; } = 0f;
    float targetY = 0f;
    int unloadTicksRemaining = 0;

    public Animal passenger { get; private set; }
    int currentTripStartTick = 0;   // currentTick when the active trip's loading completed

    // Rolling history. recentTripTicks measures the server-side trip duration (loading →
    // unloading), used by EstimatedTransitCost as the per-queue-slot cost multiplier.
    // recentEndToEndTicks measures the full mouse-perceived duration (arrival at origin
    // stop → unloaded at destination stop) — diagnostic only, used by the InfoPanel.
    // Public so SaveSystem can ToArray() / LoadFrom() them across save boundaries; size 20
    // smooths noise without lagging too much on load shifts.
    public readonly RingBufferInt recentTripTicks     = new(20);
    public readonly RingBufferInt recentEndToEndTicks = new(20);

    public class RideRequest {
        public Animal animal;
        public Node fromStop;
        public Node toStop;
        public int requestTick;       // currentTick at RequestRide time
        public int abortAtTick;       // patience deadline — past this we bail the mouse
        public RideRequest(Animal a, Node f, Node t, int tick, int abortAt) {
            animal = a; fromStop = f; toStop = t; requestTick = tick; abortAtTick = abortAt;
        }
    }
    readonly Queue<RideRequest> queue = new();
    readonly HashSet<Animal> reserved = new();   // O(1) HasReservation check

    // Tentative (plan-time) reservations — animals whose committed path traverses our
    // transit edge but who haven't yet arrived to call RequestRide. Counted into the
    // effective queue depth so simultaneous planners don't all see queue=0 and stampede
    // the elevator with bogus-low cost estimates. Graduated to `reserved` on RequestRide;
    // drained by Nav.EndNavigation if the path is abandoned.
    readonly HashSet<Animal> pendingAnimals = new();

    // Read-only accessors for InfoPanel display.
    public int QueueCountForInfo   => queue.Count;
    public int PendingCountForInfo => pendingAnimals.Count;

    // ── Construction ────────────────────────────────────────────────
    public Elevator(StructType st, int x, int y, bool mirrored = false, int shapeIndex = 0)
        : base(st, x, y, mirrored, shapeIndex: shapeIndex) {
        var graph = World.instance.graph;
        int ny = Shape.ny;
        bottomStop = graph.nodes[x, y];
        topStop = graph.nodes[x, y + ny - 1];
        // Tag both stops with this elevator so Graph.GetEdgeInfo can route through us
        // and IsNeighbor preserves the transit edge across UpdateNeighbors filtering.
        bottomStop.payload = this;
        topStop.payload = this;
        // Transit edge — direct neighbor connection between the two stop tile-nodes.
        // RebuildComponents will pick this up automatically (BFS via neighbor lists).
        bottomStop.AddNeighbor(topStop, reciprocal: true);
        _all.Add(this);
    }

    public override void OnPlaced() {
        base.OnPlaced();
        PowerSystem.instance?.RegisterConsumer(this);
    }

    public override void Destroy() {
        // Bail any waiting / riding mice cleanly so their tasks don't hang on a vanished
        // elevator. Snapshot the queue first since Animal.task.Fail() may mutate via task
        // cleanup paths.
        foreach (var rr in queue.ToArray()) {
            if (rr.animal?.nav?.ridingElevator == this) rr.animal.nav.AbortRide();
            rr.animal?.task?.Fail();
        }
        if (passenger != null && passenger.nav.ridingElevator == this) {
            passenger.nav.AbortRide();
            passenger.task?.Fail();
        }
        queue.Clear();
        reserved.Clear();
        pendingAnimals.Clear();
        passenger = null;

        // Tear down the transit edge & payload markers before the structure-claim cleanup
        // in base.Destroy(). RebuildComponents at the end re-merges the graph correctly.
        if (bottomStop != null && topStop != null) {
            bottomStop.RemoveNeighbor(topStop);
            topStop.RemoveNeighbor(bottomStop);
        }
        if (bottomStop != null) bottomStop.payload = null;
        if (topStop != null)    topStop.payload = null;
        bottomStop = null;
        topStop = null;

        _all.Remove(this);
        PowerSystem.instance?.UnregisterConsumer(this);
        base.Destroy();
        World.instance.graph.RebuildComponents();
    }

    public override void AttachAnimations() {
        // Conditional port-stub visuals — render an axle stub poking out the side
        // wherever a connected shaft is present. Same pattern as wheel/windmill.
        AttachPortStubs(Ports);

        // Platform — child GO that glides smoothly between tile rows on its own per-frame
        // lerp (driven by ElevatorPlatform). Sprite is `{name}_platform`; missing-sprite
        // case leaves the SR with null sprite (invisible) which is fine for testing — the
        // passenger still gets dragged along by the component.
        platformGO = new GameObject($"struct_{structType.name}_platform");
        platformGO.transform.SetParent(go.transform, false);
        // Platform sprite sits one tile below the passenger — visually it's the floor
        // they're standing on, not the floor they're standing in. ElevatorPlatform.Update
        // applies the same -1 offset to its lerp target.
        platformGO.transform.localPosition = new Vector3(0f, currentY - 1f, 0f);
        var psr = platformGO.AddComponent<SpriteRenderer>();
        psr.sprite = Resources.Load<Sprite>("Sprites/Buildings/" + structType.name + "_platform");
        psr.sortingOrder = sr.sortingOrder + 1;   // in front of the chassis frame
        LightReceiverUtil.SetSortBucket(psr);
        var ep = platformGO.AddComponent<ElevatorPlatform>();
        ep.elevator = this;

        // Counterweight — moves opposite to the platform. Rendered behind the chassis,
        // so it shows through hollow areas in the chassis sprite (assumed to be a frame).
        // Sprite is `{name}_counterweight`; missing → invisible, harmless.
        counterweightGO = new GameObject($"struct_{structType.name}_counterweight");
        counterweightGO.transform.SetParent(go.transform, false);
        int ny = Shape.ny;
        counterweightGO.transform.localPosition = new Vector3(0f, ny - 1 - currentY, 0f);
        var csr = counterweightGO.AddComponent<SpriteRenderer>();
        csr.sprite = Resources.Load<Sprite>("Sprites/Buildings/" + structType.name + "_counterweight");
        csr.sortingOrder = sr.sortingOrder - 1;   // behind the chassis frame
        LightReceiverUtil.SetSortBucket(csr);
        var ec = counterweightGO.AddComponent<ElevatorCounterweight>();
        ec.elevator = this;
    }

    GameObject platformGO;
    GameObject counterweightGO;

    // The platform parks at the bottom (dy=0) and at the top (dy=ny-1) of the chassis,
    // so both tiles need to be standable disembark surfaces. Middle tiles inside the
    // column stay non-standable via the default multi-tile-body rule — mice can't
    // climb the chassis itself, only ride the platform.
    public override bool HasInternalFloorAt(int localDx, int localDy) {
        if (localDx != 0) return false;
        int ny = Shape.ny;
        return localDy == 0 || localDy == ny - 1;
    }

    // ── IPowerConsumer ───────────────────────────────────────────────
    public Structure Structure => this;

    // Demand only kicks in while we're actively lifting a mouse. The empty-cabin fetch
    // (MovingToBoardingFloor) is "free" — counterweight balances the empty platform — and
    // load/unload are momentary state transitions with no movement. So an elevator sitting
    // idle, or one fetching a passenger, draws 0 from the network. Display-wise this means
    // the InfoPanel reads "consuming 0.0" almost all the time and "consuming 1.0" only
    // during the actual lift.
    public float CurrentDemand => dispatchState == DispatchState.Riding ? 1f : 0f;

    // "Could the network supply us if we asked right now?" Inclusive — returns true if
    // either we're currently allocated (rare for an idle elevator since Allocate skips
    // zero-demand consumers) OR the network's raw supply + storage discharge headroom
    // could cover nominal demand. Used by:
    //   - EstimatedTransitCost (so A* picks idle-but-connected elevators).
    //   - Idle→Trip gate (don't start trips we can't finish).
    //   - Riding advance gate. We could use a strict "actually allocated this tick" check
    //     here instead, but with binary fulfillment + allocator rotation a healthy
    //     network can still produce momentary "not allocated this tick" states that would
    //     freeze the platform mid-tile. The inclusive check smooths over those, at the
    //     cost of "moving without strictly being allocated for one tick" in pathological
    //     contended setups — accepted, since the platform catches up next tick anyway.
    //
    // Defensive on null PowerSystem (load-time edge case) — treat as powered so we don't
    // spuriously +∞-cost during world initialization.
    bool IsPowerAvailable() {
        var ps = PowerSystem.instance;
        if (ps == null) return true;
        if (ps.IsBuildingPowered(this)) return true;
        int? netId = ps.GetNetworkId(this);
        if (netId == null) return false;
        var net = ps.GetNetwork(netId.Value);
        if (net == null) return false;
        float available = net.supply;
        foreach (var st in net.storage) available += Mathf.Max(0f, st.MaxDischarge);
        return available + 1e-4f >= 1f;
    }

    public IEnumerable<PowerSystem.PowerPort> Ports {
        get {
            yield return new PowerSystem.PowerPort(-1, 0, PowerSystem.Axis.Horizontal);
            yield return new PowerSystem.PowerPort(structType.nx, 0, PowerSystem.Axis.Horizontal);
        }
    }

    // ── Transit API ──────────────────────────────────────────────────
    public bool HasReservation(Animal a) => reserved.Contains(a);

    // Plan-time tentative reservations. Animal commits to a path through this elevator
    // before arriving — we count it into wait estimates immediately so other simultaneous
    // planners see a realistic cost. Graduated to a real queue entry on RequestRide.
    public void AddTentativeReservation(Animal a) {
        if (a == null) return;
        if (reserved.Contains(a)) return;  // already past tentative; ignore
        pendingAnimals.Add(a);
    }
    public void RemoveTentativeReservation(Animal a) {
        if (a == null) return;
        pendingAnimals.Remove(a);
    }

    // Called from Nav.Move when an animal is at one stop with a path step to the other.
    // Idempotent — extra calls while already queued are no-ops.
    public bool RequestRide(Animal a, Node fromStop, Node toStop) {
        if (a == null || fromStop == null || toStop == null) return false;
        if (reserved.Contains(a)) return true;
        pendingAnimals.Remove(a);   // graduate: tentative → real queue entry
        // Patience timeout: each mouse ahead of us costs ~avgTrip ticks; allow 3× that
        // before bailing, with a 30-tick floor so cold-start mice don't bail trivially.
        // The floor also catches the unpowered case — a stalled elevator releases its
        // queue after at most 30 ticks of inactivity per mouse.
        float travelTicks = (Shape.ny - 1) / PlatformSpeed;
        float avgTrip = recentTripTicks.Average(fallback: travelTicks * 2.5f);
        int expectedWait = (int)Mathf.Ceil(queue.Count * avgTrip);
        int patience = Mathf.Max(30, 3 * expectedWait);
        int abortAt = currentTick + patience;
        queue.Enqueue(new RideRequest(a, fromStop, toStop, currentTick, abortAt));
        reserved.Add(a);
        return true;
    }

    // A*'s view of how expensive this transit edge is. Three components:
    //   travelTicks: deterministic — verticalSpan / PlatformSpeed.
    //   avgTrip:     rolling-history average of recent trip durations (load + travel +
    //                unload + return). Cold-start fallback is travelTicks * 2.5 — optimistic
    //                so the elevator gets used and history accumulates; pessimism would
    //                permanently freeze it out before any real data arrives.
    //   queueDepth:  reserved (in-flight on the elevator) + pendingAnimals (committed path
    //                but not yet arrived). Each queued mouse roughly costs one avgTrip.
    // Returns +∞ when unpowered so A* drops the elevator from its candidate set; mice plan
    // around it (via stairs/ladders if available) instead of committing to a doomed ride.
    public float EstimatedTransitCost(Node from, Node to) {
        if (!IsPowerAvailable()) return float.PositiveInfinity;
        float travelTicks = Mathf.Abs(to.wy - from.wy) / PlatformSpeed;
        float avgTrip = recentTripTicks.Average(fallback: travelTicks * 2.5f);
        int queueDepth = queue.Count + pendingAnimals.Count;
        float waitTicks = queueDepth * avgTrip;
        return travelTicks + waitTicks;
    }

    // ── Per-tick state machine ───────────────────────────────────────
    // Loop until we hit a "rest" point. Movement-bearing cases (MovingToBoardingFloor,
    // Riding) always return right after their single AdvanceTowards, so the platform
    // never advances more than PlatformSpeed tiles in a tick. Instant transitions
    // (Idle → trip start, Unloading → Idle → next trip) cascade via `continue` so a
    // mouse arriving at a parked platform can board AND get the first riding step in
    // the same tick — eliminating the old ~2-tick delay.
    public void Tick() {
        AbortStaleQueueEntries();
        while (true) {
            switch (dispatchState) {
                case DispatchState.Idle:
                    if (queue.Count == 0) return;
                    // Don't start a trip we can't finish. Inclusive check — at this point our
                    // demand is still 0, so we wouldn't be allocated yet. We're asking "is the
                    // network healthy enough to power us once we're actually riding?"
                    if (!IsPowerAvailable()) return;
                    StartTrip(queue.Peek());
                    continue;          // re-process new state (Riding or MovingToBoardingFloor)

                case DispatchState.MovingToBoardingFloor:
                    // No power gate — empty cabin / counterweight, demand=0. Move freely.
                    AdvanceTowards(targetY);
                    if (Mathf.Approximately(currentY, targetY)) {
                        // Skip the old "Loading" tick — board the passenger as part of the
                        // arrival. The first Riding advance waits until next tick (we've
                        // already used this tick's movement step on the descent's last leg),
                        // giving a brief visible pause for boarding.
                        BoardPassenger();
                    }
                    return;

                case DispatchState.Riding:
                    // Inclusive gate — pause only when the network truly can't supply us.
                    // Briefly losing allocator rotation to another consumer (1-tick gap)
                    // doesn't freeze the platform mid-trip; only a genuinely starved net does.
                    if (!IsPowerAvailable()) return;
                    AdvanceTowards(targetY);
                    if (Mathf.Approximately(currentY, targetY)) {
                        dispatchState = DispatchState.Unloading;
                        unloadTicksRemaining = 1;
                    }
                    return;

                case DispatchState.Unloading:
                    if (--unloadTicksRemaining > 0) return;
                    CompleteTrip();
                    continue;          // state Idle now — try to start the next trip in same tick
            }
        }
    }

    // Patience timeout — bail mice whose abortAtTick has passed. Skips the head when it's
    // actively being served (state ≠ Idle), since at that point the platform is committed
    // to that trip. Mice queued behind a stale head can still abort. Called at the top of
    // each Tick(); rebuilds the queue in O(N) which is fine for small queues (~5 entries).
    void AbortStaleQueueEntries() {
        if (queue.Count == 0) return;
        var snapshot = queue.ToArray();
        bool any = false;
        for (int i = 0; i < snapshot.Length; i++) {
            bool isActiveHead = i == 0 && dispatchState != DispatchState.Idle;
            if (isActiveHead) continue;
            if (currentTick < snapshot[i].abortAtTick) continue;
            // Stale — bail this mouse.
            BailRequest(snapshot[i]);
            snapshot[i] = null;
            any = true;
        }
        if (!any) return;
        queue.Clear();
        for (int i = 0; i < snapshot.Length; i++)
            if (snapshot[i] != null) queue.Enqueue(snapshot[i]);
    }

    void BailRequest(RideRequest rr) {
        reserved.Remove(rr.animal);
        if (rr.animal == null) return;
        rr.animal.nav?.AbortRide();
        rr.animal.task?.Fail();
    }

    void StartTrip(RideRequest next) {
        targetY = next.fromStop.wy - y;          // local platform coord of the boarding floor
        if (Mathf.Approximately(currentY, targetY)) {
            // Already there — board immediately and transition to Riding. The cascading
            // Tick loop will run the first Riding advance in this same tick.
            BoardPassenger();
        } else {
            dispatchState = DispatchState.MovingToBoardingFloor;
        }
    }

    void BoardPassenger() {
        var next = queue.Peek();
        passenger = next.animal;
        if (passenger.nav != null) passenger.nav.ridingElevator = this;
        SnapPassengerToPlatform();
        currentTripStartTick = currentTick;
        targetY = next.toStop.wy - y;
        dispatchState = DispatchState.Riding;
    }

    void CompleteTrip() {
        var done = queue.Dequeue();
        reserved.Remove(done.animal);
        // Two history samples per completed trip:
        //  - recentTripTicks: load → unload (server-side trip duration, used in cost).
        //  - recentEndToEndTicks: request → unload (full mouse-perceived duration, for
        //    diagnostic display — InfoPanel in Phase 5).
        recentTripTicks.Add(currentTick - currentTripStartTick);
        recentEndToEndTicks.Add(currentTick - done.requestTick);
        if (passenger != null) {
            // Hand control back to Nav. The animal is at the destination stop's position;
            // the next Move() call advances pathIndex past the transit edge naturally.
            passenger.nav?.OnTransitComplete(done.toStop);
            passenger = null;
        }
        dispatchState = DispatchState.Idle;
    }

    void AdvanceTowards(float t) {
        currentY = Mathf.MoveTowards(currentY, t, PlatformSpeed);
    }

    // Initial sync at boarding: snap passenger to the platform's current world position
    // so the rider doesn't visibly jump when the per-frame ElevatorPlatform.Update first
    // takes over. After this, passenger position is owned by ElevatorPlatform until
    // CompleteTrip clears the riding state.
    void SnapPassengerToPlatform() {
        if (passenger == null) return;
        passenger.x = x;
        passenger.y = y + currentY;
        if (passenger.go != null)
            passenger.go.transform.position = new Vector3(passenger.x, passenger.y, passenger.z);
    }
}
