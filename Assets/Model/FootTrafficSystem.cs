using UnityEngine;

// Foot-traffic data layer — a pure C# singleton that records how often each tile is
// occupied by a MOVING mouse, feeding the foot-traffic data overlay (OverlayController).
//
// One float per tile (intensity). Once per in-game second (the coarse, cheap cadence this
// view wants — accuracy isn't the point) we:
//   1. decay the whole field toward zero with a long half-life, so stale traffic fades, then
//   2. add a unit sample at each currently-moving mouse's tile.
// The field settles roughly proportional to how frequently a tile carries through-traffic.
// The overlay normalises against the live max, so the absolute scale here is cosmetic — only
// the relative pattern matters.
//
// Sampling runs every second regardless of whether the overlay is open, so the heat map is
// already warm when the player opens it. It's not saved: a transient visualisation that
// re-converges within a minute of play, so it's simply cleared on world teardown and rebuilds
// after a load.
public class FootTrafficSystem {
    public static FootTrafficSystem instance { get; private set; }

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void ResetStatics() { instance = null; }

    // Half-life of a tile's traffic intensity, in real seconds. Long, so the map reads as a
    // slow heat map rather than flickering as each mouse passes. Decay runs once per second,
    // so per-second retention = 0.5^(1/HalfLifeSeconds).
    private const float HalfLifeSeconds = 45f;
    private static readonly float PerSecondRetention = Mathf.Pow(0.5f, 1f / HalfLifeSeconds);

    private float[] _intensity;   // [y*nx + x]; lazily (re)sized to the world
    private int _nx, _ny;

    public static FootTrafficSystem Create() {
        instance = new FootTrafficSystem();
        return instance;
    }

    private void EnsureSized() {
        World w = World.instance;
        if (w == null) return;
        if (_intensity == null || _nx != w.nx || _ny != w.ny) {
            _nx = w.nx; _ny = w.ny;
            _intensity = new float[_nx * _ny];
        }
    }

    // Called once per in-game second from World.Tick.
    public void Sample() {
        World w = World.instance;
        if (w == null) return;
        EnsureSized();

        // 1) Decay the whole field toward zero.
        float k = PerSecondRetention;
        for (int i = 0; i < _intensity.Length; i++) _intensity[i] *= k;

        // 2) Credit each moving mouse's current tile (same float→tile rounding as World.GetTileAt).
        var ac = AnimalController.instance;
        if (ac == null || ac.animals == null) return;
        foreach (var a in ac.animals) {
            if (a == null || !a.IsMoving()) continue;
            int tx = Mathf.FloorToInt(a.x + 0.5f);
            int ty = Mathf.FloorToInt(a.y + 0.5f);
            if (tx < 0 || tx >= _nx || ty < 0 || ty >= _ny) continue;
            _intensity[ty * _nx + tx] += 1f;
        }
    }

    // Raw intensity at a tile (0 = never trafficked). Out-of-range → 0.
    public float IntensityAt(int x, int y) {
        if (_intensity == null || x < 0 || x >= _nx || y < 0 || y >= _ny) return 0f;
        return _intensity[y * _nx + x];
    }

    // Estimated traffic at a tile in mice/hour, for the hover readout. Intensity is a decaying
    // occupancy EMA: at steady state intensity ≈ occupancy/(1-retention), so intensity*(1-retention)
    // is the per-second mouse-occupancy, scaled up to one in-game hour. It reads as mouse-seconds of
    // occupancy per hour — which equals mice/hour when a mouse dwells ~1 s per tile (close enough for
    // a coarse heat-map readout).
    public float MicePerHourAt(int x, int y) {
        float perSecond = IntensityAt(x, y) * (1f - PerSecondRetention);
        return perSecond * (World.ticksInDay / 24f);
    }

    // Largest intensity in the field — the overlay normalises against this so the busiest tile
    // is always full-bright regardless of the absolute (cadence-dependent) scale.
    public float MaxIntensity() {
        if (_intensity == null) return 0f;
        float m = 0f;
        for (int i = 0; i < _intensity.Length; i++) if (_intensity[i] > m) m = _intensity[i];
        return m;
    }

    public void Clear() {
        if (_intensity != null) System.Array.Clear(_intensity, 0, _intensity.Length);
    }
}
