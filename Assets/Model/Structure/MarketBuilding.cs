using UnityEngine;

// The one market building in the world — set as the off-screen portal at world gen.
// Provides a global accessor so SaveSystem and other systems can reach it directly
// without scanning the world or doing a pathfinding lookup.
public class MarketBuilding : Building {
    public static MarketBuilding instance { get; private set; }

    // Reload-Domain-off support — see MaintenanceSystem.ResetStatics for the why.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }

    public MarketBuilding(StructType st, int x, int y, bool mirrored = false)
        : base(st, x, y, mirrored) {
        if (instance != null)
            Debug.LogError("MarketBuilding: second market placed — only one should exist.");
        instance = this;
    }

    public override void Destroy() {
        base.Destroy();
        if (instance == this) instance = null;
    }
}
