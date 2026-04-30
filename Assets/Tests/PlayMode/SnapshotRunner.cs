using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Snapshot-test runner. Drives the simulation deterministically and compares the
// resulting world state against a checked-in golden JSON file.
//
// Mode: RunDefaultWorld(unitySeed, ticks, name) — bypasses auto-load via
//   WorldController.skipAutoLoad, seeds UnityEngine.Random so GenerateDefault
//   picks the same world seed every run, then ticks N times. Use for "fresh
//   world" scenarios where you don't need specific tiles/buildings/animals
//   pre-authored. (Future RunFromScenario will accept a saved JSON for richer
//   scenarios — the SerializeToJson / LoadFromJson plumbing is already in place.)
//
// Golden file workflow:
//   - First run: golden doesn't exist → runner writes the captured state as the
//     golden and marks the test Inconclusive. Review the file, commit if good.
//   - Subsequent runs: compare actual vs golden. On mismatch, write the actual to
//     Application.temporaryCachePath (NOT under Assets/, so no .meta noise) and
//     fail with the file path. Use a diff tool to inspect.
//
// To accept new state as the new golden after intentional behavior changes:
// delete the .golden.json file and re-run.
public static class SnapshotRunner {
    static string ScenariosDir => Application.dataPath + "/Tests/PlayMode/Scenarios";

    public static IEnumerator RunDefaultWorld(int unitySeed, int ticks, string name) {
        // Pause Unity's frame loop so World.Update / Animal.Update don't run alongside
        // our explicit ticks. Without this, Time.deltaTime variability between fixed-step
        // calls produces sub-millisecond drift across runs. yield return null still works
        // (frame-based, not time-based).
        float savedTimeScale = Time.timeScale;
        Time.timeScale = 0f;
        // Unity domain may not reload between PlayMode test runs (project setting), so
        // singleton-ctor "two instances of X" LogErrors fire on second-and-later runs.
        // Mop up: ignore failing log messages during scene setup. Snapshot comparison
        // is the real assertion — log noise during setup doesn't affect correctness.
        LogAssert.ignoreFailingMessages = true;
        // Skip the WorldController auto-load (which would pick up the user's most-recent
        // save and put us in an arbitrary playthrough state) — we want a fresh world.
        WorldController.skipAutoLoad = true;
        // Seed BEFORE scene load so WorldController.GenerateDefault's
        // `UnityEngine.Random.Range(1, 100000)` picks a deterministic world seed.
        UnityEngine.Random.InitState(unitySeed);

        try {
            NullStaticInstances();

            yield return SceneManager.LoadSceneAsync("Assets/Scenes/Main.unity", LoadSceneMode.Single);
            // 4 frames covers WorldController.Start's `yield return null` + GenerateDefault
            // running on the next frame + PostLoadInit's yield + Animal.Start firing on
            // frame 2. The world is fully ready after this.
            yield return null;
            yield return null;
            yield return null;
            yield return null;

            const float dt = 1f / 60f;
            for (int i = 0; i < ticks; i++) {
                World.instance.Tick(dt);
                AnimalController.instance?.Tick(dt);
            }

            string actual = SaveSystem.instance.SerializeToJson();
            AssertMatchesGolden(actual, name);
        }
        finally {
            Time.timeScale = savedTimeScale;
            LogAssert.ignoreFailingMessages = false;
            WorldController.skipAutoLoad = false;
        }
    }

    // Clears the static `instance` field on every type that follows the project's
    // singleton pattern. Without this, a second snapshot run within the same Unity
    // session sees the previous instance and the new ctor's duplicate-detection
    // LogError ("there should only be one X") fires. ignoreFailingMessages mops up
    // any we miss; this catches the obvious ones cleanly.
    static void NullStaticInstances() {
        System.Type[] types = {
            typeof(World), typeof(Db), typeof(GlobalInventory),
            typeof(WorldController), typeof(AnimalController), typeof(StructController),
            typeof(InventoryController), typeof(PlantController), typeof(WaterController),
            typeof(WeatherSystem), typeof(MaintenanceSystem), typeof(MoistureSystem),
            typeof(PowerSystem), typeof(ResearchSystem), typeof(WorkOrderManager),
            typeof(SaveSystem), typeof(RecipePanel), typeof(InfoPanel), typeof(UI),
        };
        foreach (System.Type t in types) {
            PropertyInfo prop = t.GetProperty("instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (prop != null) {
                MethodInfo setter = prop.GetSetMethod(nonPublic: true);
                setter?.Invoke(null, new object[] { null });
            }
        }
    }

    static void AssertMatchesGolden(string actual, string name) {
        if (!Directory.Exists(ScenariosDir)) Directory.CreateDirectory(ScenariosDir);
        string goldenPath = System.IO.Path.Combine(ScenariosDir, name + ".golden.json");
        string actualPath = System.IO.Path.Combine(Application.temporaryCachePath, name + ".actual.json");

        if (!File.Exists(goldenPath)) {
            File.WriteAllText(goldenPath, actual);
            Assert.Inconclusive(
                $"Initial golden written to {goldenPath}. Review the contents, then re-run; " +
                "subsequent runs will diff against it."
            );
            return;
        }

        string golden = File.ReadAllText(goldenPath);
        if (actual == golden) {
            if (File.Exists(actualPath)) File.Delete(actualPath);
            return;
        }

        File.WriteAllText(actualPath, actual);
        Assert.Fail(
            $"Snapshot diverged for '{name}'.\n" +
            $"  golden: {goldenPath}\n" +
            $"  actual: {actualPath}\n" +
            "Use a diff tool to compare. If the new state is intended, delete the golden and re-run to re-baseline."
        );
    }
}
