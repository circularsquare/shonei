using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;

// Smoke test for the Tier 3 snapshot harness foundation. Loads Main.unity (which wires
// up all controllers), waits for WorldController to finish frame-2 init, then drives
// World.Tick(1/60f) for 10 simulated seconds and verifies the world advances cleanly.
//
// Snapshot diffing against a golden file is the next layer up — see Tier 3 plan.
public class TickSmokeTest {

    [UnityTest]
    public IEnumerator Tick_AdvancesTimer_NoExceptions() {
        // Main.unity isn't in Build Settings (only the stale SampleScene is), so load by path.
        // Works in editor PlayMode tests; standalone test builds would need it added.
        yield return SceneManager.LoadSceneAsync("Assets/Scenes/Main.unity", LoadSceneMode.Single);
        yield return null;
        yield return null;
        yield return null;

        World world = World.instance;
        Assert.That(world, Is.Not.Null, "World.instance should be set after Main scene loads");
        float startTimer = world.timer;
        int startAnimals = AnimalController.instance != null ? AnimalController.instance.na : 0;

        const float dt = 1f / 60f;
        for (int i = 0; i < 600; i++) {
            world.Tick(dt);
            if (AnimalController.instance != null) AnimalController.instance.Tick(dt);
        }

        Assert.That(world.timer, Is.GreaterThan(startTimer + 9f), "world.timer should advance ~10s");
        Assert.That(world.timer, Is.LessThan(startTimer + 11f), "world.timer should not overshoot");
        if (AnimalController.instance != null)
            Assert.That(AnimalController.instance.na, Is.EqualTo(startAnimals), "animal count should be stable");
    }
}
