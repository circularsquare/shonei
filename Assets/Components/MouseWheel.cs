using System.Collections.Generic;
using System.Linq;

// A treadmill-style power producer. A "runner" mouse comes here via the standard
// CraftTask machinery (one zero-IO recipe authored in recipesDb.json). While the
// craft order has at least one reserved seat, the wheel produces 1.0 power into
// whatever shaft network it's attached to.
//
// Footprint: 2×2 (anchor bottom-left). Power port: one tile to the right of the
// anchor at mid-height — i.e. just outside the wheel's right edge. Mirroring flips
// the port to the left side automatically (PowerSystem.FindAttachedNetwork handles it).
//
// Animation hook (deferred): IsCurrentlyActive can drive a wheel-rotation visual.
public class MouseWheel : Building, PowerSystem.IPowerProducer {
    // Scalar power produced while a worker is on the wheel. 1.0 = "one mouse-second of power".
    // Tunable; set high enough that one wheel can power one consumer (powerBoost target = 1.0).
    public const float Output = 1.0f;

    // Cached craft order — looked up lazily on first IsCurrentlyActive read after placement,
    // since OnPlaced registers the order but we don't want to assume registration order.
    WorkOrderManager.WorkOrder cachedCraftOrder;

    public MouseWheel(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    public override void OnPlaced() {
        base.OnPlaced(); // registers WOM craft order
        PowerSystem.instance?.RegisterProducer(this);
    }

    public override void Destroy() {
        PowerSystem.instance?.UnregisterProducer(this);
        base.Destroy();
    }

    // True iff at least one mouse currently has the craft order reserved (i.e. a runner
    // is engaged with the wheel). Reads WorkOrder.res.reserved which CraftTask claims
    // on dispatch and releases on cleanup.
    public bool IsCurrentlyActive {
        get {
            if (cachedCraftOrder == null) {
                var wom = WorkOrderManager.instance;
                if (wom == null) return false;
                cachedCraftOrder = wom.FindOrdersForBuilding(this)
                    .FirstOrDefault(o => o.type == WorkOrderManager.OrderType.Craft);
            }
            return cachedCraftOrder != null && cachedCraftOrder.res != null && cachedCraftOrder.res.reserved > 0;
        }
    }

    public override void AttachAnimations() {
        AttachFrameAnimator("wheel", () => IsCurrentlyActive, baseFps: 8f);
    }

    // ── IPowerProducer ────────────────────────────────────────────────
    public Structure Structure => this;
    public float CurrentOutput => IsCurrentlyActive ? Output : 0f;

    public IEnumerable<PowerSystem.PowerPort> Ports {
        get {
            // One horizontal port on the LEFT side of the wheel, bottom row. The wheel
            // sprite includes a visual indicator on this side so players can see where
            // the shaft attaches. Mirroring (F) flips the port to the right side via
            // PowerSystem.FindAttachedNetwork's standard mirror handling.
            yield return new PowerSystem.PowerPort(-1, 0, PowerSystem.Axis.Horizontal);
        }
    }
}
