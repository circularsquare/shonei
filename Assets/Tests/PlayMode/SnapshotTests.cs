using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

// Snapshot tests. Each test names a scenario; the captured world state lives in
// Assets/Tests/PlayMode/Scenarios/<name>.golden.json. See SnapshotRunner.cs for
// the workflow (first run writes the golden and is Inconclusive; subsequent runs
// diff against it).
//
// Adding scenarios: keep them lean. Each scenario should exercise a coherent
// slice of behaviour (worldgen + animal AI + tick dispatch, market trade cycle,
// research progression, etc.) — not duplicate what unit tests already cover.
public class SnapshotTests {

    // Default world generated with a pinned Unity-seed, ticked for 10 simulated
    // seconds. Catches regressions in: worldgen, animal spawning, animal Start
    // initialization, tick dispatch (1s / 0.2s / 10s / hourly cadences), per-animal
    // RNG seeding, and the save serialization format.
    [UnityTest]
    public IEnumerator DefaultWorld_TenSeconds_Stable() {
        yield return SnapshotRunner.RunDefaultWorld(unitySeed: 12345, ticks: 600, name: "default_world_10s");
    }

    // Same world, ~6x longer run. The 10s scenario covers startup determinism;
    // this one probes drift over time -- accumulation bugs in inventory bookkeeping,
    // decay rounding, or RNG state divergence that only show up after many ticks.
    // Same seed as the 10s test on purpose: any divergence here that doesn't show
    // up at 10s is by definition a slow-drift regression, not a worldgen one.
    [UnityTest]
    public IEnumerator DefaultWorld_SixtySeconds_Stable() {
        yield return SnapshotRunner.RunDefaultWorld(unitySeed: 12345, ticks: 3600, name: "default_world_60s");
    }
}
