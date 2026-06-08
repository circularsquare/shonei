#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

// Dev-only shortcuts for hopping between the two scenes during Phase 1 work.
// Tools/Scene/{Open Menu, Open Main, Play From Menu}. Each prompts to save the
// current scene first (standard Unity save dialog) before switching.
static class SceneMenu {
    const string MenuPath = "Assets/Scenes/Menu.unity";
    const string MainPath = "Assets/Scenes/Main.unity";

    [MenuItem("Tools/Scene/Open Menu", false, 0)]
    static void OpenMenu() { Open(MenuPath); }

    [MenuItem("Tools/Scene/Open Main", false, 1)]
    static void OpenMain() { Open(MainPath); }

    // Open the menu scene and enter Play — the usual login-flow test path.
    [MenuItem("Tools/Scene/Play From Menu", false, 20)]
    static void PlayFromMenu() {
        if (Open(MenuPath)) EditorApplication.isPlaying = true;
    }

    static bool Open(string path) {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false; // user cancelled
        EditorSceneManager.OpenScene(path);
        return true;
    }
}
#endif
