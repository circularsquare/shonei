using UnityEngine;

// Global toggle for in-game debug info. When off (the default), developer-only
// readouts are hidden from the player: work-order references in the InfoPanel,
// and animal/tile internals (location, task/objective, pathfinding).
//
// Toggled with Ctrl+D (see MouseController), which also dumps an audit log to the
// console. This is distinct from the F3 graphics-stats overlay (GpuStatsHUD) —
// that's GPU/render performance, this is gameplay/debug info.
public static class DebugMode {
    static bool enabled;

    public static bool Enabled => enabled;

    // Fires whenever Enabled flips, so UI that's built once (e.g. the InfoPanel's
    // active view) can react without polling. Text that
    // already rebuilds every tick doesn't strictly need it, but the InfoPanel
    // subscribes so the toggle also takes effect while the game is paused.
    public static event System.Action Changed;

    public static void Toggle() {
        enabled = !enabled;
        Changed?.Invoke();
    }

    // Statics survive play-session restarts when domain reload is disabled; reset
    // so debug mode never leaks across sessions and stale subscribers are dropped.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() {
        enabled = false;
        Changed = null;
    }
}
