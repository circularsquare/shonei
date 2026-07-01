# Testing

How tests are organised and run. For *when* it's worth running them at all, see the **Testing** section in `CLAUDE.md` — many impactful bugs only surface in playtesting, so tests are a help, not a per-change gate.

## Test types

**EditMode tests** (`Assets/Tests/Editor/`) — one file per system (e.g. `ItemStackTests.cs`). Fast (ms each). Use for pure-logic invariants — fen/liang math, recipe scoring, inventory bookkeeping. Cannot use Unity lifecycle (`Start` doesn't fire); singletons must be wired via reflection helpers.

**PlayMode tests** (`Assets/Tests/PlayMode/`) — load `Main.unity`, run actual game lifecycle. Slower (seconds each). Use for integration / snapshot tests where Animal AI, scene-loaded controllers, or the real save/load path matter. `TickSmokeTest.cs` is the canonical example: load Main → wait 3 frames → drive `World.Tick(1/60f)` × N → assert state.

**Snapshot tests** (`Assets/Tests/PlayMode/SnapshotTests.cs` + `SnapshotRunner.cs`) — **currently paused per user preference; don't suggest running them or re-baselining goldens until re-enabled.** The machinery below stays documented for when they come back. They capture full world state as JSON, diff against a checked-in golden file. Catches regressions in *any* system that affects serialized state (worldgen, animal AI, tick dispatch, save format) without writing per-system assertions. Goldens live in `Assets/Tests/PlayMode/Scenarios/<name>.golden.json`. On mismatch, the actual is written to `Application.temporaryCachePath` for diffing.

To add a new snapshot scenario:
1. Add a `[UnityTest]` method to `SnapshotTests.cs` that calls `SnapshotRunner.RunDefaultWorld(unitySeed: <fixed>, ticks: <N>, name: <unique>)`.
2. Run it once — golden is written and the test reports Inconclusive. Review the golden file, commit if good.
3. Subsequent runs diff against the golden. To accept new state after intentional behavior changes, delete the golden and re-run.

The runner pauses `Time.timeScale`, sets `WorldController.skipAutoLoad` so the user's most-recent save isn't picked up, and nulls singleton statics to keep state clean across consecutive runs in the same Unity session. If you add a new singleton that surfaces a "two instances of X" error during snapshot tests, add its type to `NullStaticInstances` in `SnapshotRunner.cs`.

## Running

**Via Unity MCP** (`mcp__unity__*`):
- `run_tests` returns a job_id; poll with `get_test_job` (use `wait_timeout: 60` and `include_failed_tests: true`). Specify `mode: "EditMode"` or `mode: "PlayMode"`.
- **Never auto-invoke `run_tests`** — it triggers a recompile / domain reload that interrupts in-flight editor work, and can't run while Unity is in Play Mode. Suggest it and wait for explicit confirmation. (Also in CLAUDE.md.)

**Headless CLI**: `Tools/run-tests.bat [EditMode|PlayMode|all]` runs tests without opening the editor. Useful for ad-hoc runs and future CI. Requires Unity to be closed (it locks `Library/`). Output: `TestResults/<platform>.xml` (gitignored). Override Unity path with `UNITY_PATH` env var.

## Adding tests

- One test class per system, named `SystemNameTests.cs`. Use existing files as the style reference.
- **Keep them lean.** A bug fix gets ONE test that would have caught it. A new feature gets a small handful covering the contract — not an exhaustive matrix. Tests are read-mostly: pad them and you pay the cost forever.
- Cover the *invariant* or *contract*, not every getter or trivial branch. Heavy `[TestCase]` parameterization beats many copy-paste `[Test]` methods.
- For protected-set static singletons (`Db.itemByName`, `RecipePanel.instance`, etc.), use the `SetSingletonInstance` / `SetStaticProp` reflection helpers in existing files — copy the pattern, don't reinvent.
- EditMode tests for methods that touch `World.instance.timer` or require a live `Animal`/`Inventory`: skip with a clearly-marked `// Deferred` comment block, OR write them as PlayMode tests. Don't fight the dependency in unit-test setup.

**When a test fails**: diagnose the regression and fix the code. Don't change assertions to make them pass unless the test itself was wrong (rare; verify carefully).
