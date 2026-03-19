using UnityEditor;
using System.Reflection;

public static class ClearConsole {
    // Ctrl+L to clear the Unity console
    [MenuItem("Edit/Clear Console %l")]
    static void Clear() {
        var assembly = Assembly.GetAssembly(typeof(SceneView));
        var type = assembly.GetType("UnityEditor.LogEntries");
        var method = type.GetMethod("Clear");
        method.Invoke(null, null);
    }
}
