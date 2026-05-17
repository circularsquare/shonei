using UnityEngine;

// Development tool: in-game panel for iterating on WorldGen parameters with a
// pinned seed across recompiles. Workflow:
//
//   1. Press play. Enable "Sticky mode" in the panel (top-right, F9 toggles
//      visibility). From now on:
//        - Auto-load on play is skipped (you get a fresh worldgen, not your save)
//        - The seed is persisted to PlayerPrefs across domain reloads
//        - "Same seed" regenerates the world using the persisted seed
//        - "New seed" rolls a new random seed
//   2. Edit a WorldGen constant in code. On save, Unity recompiles → domain
//      reloads → static fields reset. BeforeSceneLoad bootstrap below pulls
//      stickyWorldgen + lastGeneratedSeed back out of PlayerPrefs, so the
//      auto-generation that runs on play resumes with the same seed.
//   3. Disable "Sticky mode" when done. Auto-load comes back, random seeds
//      come back.
//
// Production note: nothing in this file or in the WorldController hooks runs
// unless stickyWorldgen is true OR pendingSeedOverride is set. Tests set
// skipAutoLoad directly without going through here.
public class WorldGenDebugPanel : MonoBehaviour {
    public const string StickyKey   = "Shonei.WorldGenDebug.Sticky";
    public const string LastSeedKey = "Shonei.WorldGenDebug.LastSeed";

    bool visible = false;
    bool sticky;
    Rect windowRect = new Rect(0, 0, 240, 0); // x positioned in OnGUI from Screen.width

    // Runs before Main scene's MonoBehaviour Awake/Start. Pulls persisted dev
    // state into WorldController statics so the world's initial auto-generation
    // (which happens after this) sees the pinned seed.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void BootstrapStaticsFromPrefs() {
        bool persistedSticky = PlayerPrefs.GetInt(StickyKey, 0) == 1;
        WorldController.stickyWorldgen = persistedSticky;
        if (persistedSticky) {
            // Sticky implies skip-auto-load: otherwise recompile would land you
            // back in your most-recent save instead of regenerating the world.
            WorldController.skipAutoLoad = true;
            WorldController.lastGeneratedSeed = PlayerPrefs.GetInt(LastSeedKey, 0);
        }
    }

    // Spawn the panel GameObject after the scene loads so OnGUI fires.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreatePanel() {
        GameObject go = new GameObject("WorldGenDebugPanel");
        go.AddComponent<WorldGenDebugPanel>();
        DontDestroyOnLoad(go);
    }

    void Awake() {
        sticky = PlayerPrefs.GetInt(StickyKey, 0) == 1;
    }

    void Update() {
        if (Input.GetKeyDown(KeyCode.F9)) visible = !visible;
    }

    void OnGUI() {
        if (!visible) return;
        // Position dynamically each frame — Screen.width can change with window resize.
        windowRect.x = Screen.width - windowRect.width - 10;
        windowRect.y = 10;
        windowRect = GUILayout.Window(WindowIdMagic, windowRect, DrawWindow, "WorldGen Debug (F9 to hide)");
    }

    // GUILayout.Window needs a stable int ID; pick something unlikely to collide.
    const int WindowIdMagic = 0x70_72_0D;

    void DrawWindow(int id) {
        GUILayout.Label($"Seed: {WorldController.lastGeneratedSeed}");

        bool newSticky = GUILayout.Toggle(sticky, " Sticky mode");
        if (newSticky != sticky) {
            sticky = newSticky;
            PlayerPrefs.SetInt(StickyKey, sticky ? 1 : 0);
            WorldController.stickyWorldgen = sticky;
            // skipAutoLoad applies to the *next* scene load only; flipping it
            // here is harmless because the world is already loaded. The
            // BeforeSceneLoad bootstrap re-applies it on next play.
            if (sticky) WorldController.skipAutoLoad = true;
        }

        GUI.enabled = WorldController.lastGeneratedSeed != 0;
        if (GUILayout.Button("Regenerate (same seed)")) {
            WorldController.pendingSeedOverride = WorldController.lastGeneratedSeed;
            SaveSystem.instance.LoadDefault();
        }
        GUI.enabled = true;

        if (GUILayout.Button("Regenerate (new seed)")) {
            WorldController.pendingSeedOverride = UnityEngine.Random.Range(1, 100000);
            SaveSystem.instance.LoadDefault();
        }

        GUI.DragWindow();
    }
}
