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
}
