#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

// Dev-only scene-flow tooling. Tools/Scene/{Open Menu, Open Main}, plus a
// "Start Play in Menu" toggle. When that toggle is on (the default), Unity's
// playModeStartScene routes EVERY Play-mode entry through Menu.unity regardless of
// which scene is open — so pressing Play from Main goes through the real login /
// Continue flow instead of booting straight into a save-less, freshly generated
// world. Turn it off to iterate on Main directly. The setting is editor-only and
// persisted per-machine in EditorPrefs.
[InitializeOnLoad]
static class SceneMenu {
    const string MenuPath = "Assets/Scenes/Menu.unity";
    const string MainPath = "Assets/Scenes/Main.unity";
    const string StartInMenuItem = "Tools/Scene/Start Play in Menu";
    const string StartInMenuPref  = "Shonei.PlayStartsInMenu";

    // InitializeOnLoad runs this on editor load and after every recompile. AssetDatabase
    // isn't reliably ready inside the static ctor, so defer the lookup one tick.
    static SceneMenu() { EditorApplication.delayCall += ApplyPlayModeStartScene; }

    [MenuItem("Tools/Scene/Open Menu", false, 0)]
    static void OpenMenu() { Open(MenuPath); }

    [MenuItem("Tools/Scene/Open Main", false, 1)]
    static void OpenMain() { Open(MainPath); }

    [MenuItem(StartInMenuItem, false, 21)]
    static void ToggleStartInMenu() {
        EditorPrefs.SetBool(StartInMenuPref, !StartInMenuEnabled);
        ApplyPlayModeStartScene();
    }

    // Draw the checkmark to reflect the current state.
    [MenuItem(StartInMenuItem, true)]
    static bool ToggleStartInMenuValidate() {
        UnityEditor.Menu.SetChecked(StartInMenuItem, StartInMenuEnabled);
        return true;
    }

    static bool StartInMenuEnabled => EditorPrefs.GetBool(StartInMenuPref, true);

    static void ApplyPlayModeStartScene() {
        EditorSceneManager.playModeStartScene = StartInMenuEnabled
            ? AssetDatabase.LoadAssetAtPath<SceneAsset>(MenuPath)
            : null; // null → Unity plays the currently-open scene
    }

    static bool Open(string path) {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false; // user cancelled
        EditorSceneManager.OpenScene(path);
        return true;
    }
}
#endif
