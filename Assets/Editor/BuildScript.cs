using System;
using System.IO;
using System.Linq;
using IOPath = System.IO.Path;   // the project has its own `Path` (A* pathfinding) that shadows System.IO.Path
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Headless build entry points for Shonei, invoked by Tools/build-and-publish.ps1
// via Unity's -executeMethod. Also exposed as Tools/Build/* menu items for manual
// one-off builds from inside the editor.
//
// Output layout is chosen so butler can push each platform folder directly:
//   <buildsDir>/win/Shonei/Shonei.exe (+ data)   push <buildsDir>/win  -> archive root = Shonei/
//   <buildsDir>/mac/Shonei.app                    push <buildsDir>/mac  -> archive root = Shonei.app
// The wrapper Shonei/ folder keeps a careless "Extract All" tidy on Windows; on
// Mac the .app bundle is itself the single shippable item.
//
// buildsDir comes from the "-buildsDir <path>" CLI arg the ps1 passes; absent
// (e.g. a manual menu build) it defaults to "<projectRoot>/../builds".
public static class BuildScript {
    const string ProductFolder = "Shonei";   // wrapper folder + exe/app base name

    [MenuItem("Tools/Build/Windows")]
    public static void BuildWindows() {
        string exe = IOPath.Combine(BuildsDir(), "win", ProductFolder, ProductFolder + ".exe");
        Build(BuildTarget.StandaloneWindows64, exe);
    }

    [MenuItem("Tools/Build/Mac")]
    public static void BuildMac() {
        string app = IOPath.Combine(BuildsDir(), "mac", ProductFolder + ".app");
        Build(BuildTarget.StandaloneOSX, app);
    }

    // Both platforms in one editor launch — what the publish script calls.
    public static void BuildAll() {
        BuildWindows();
        BuildMac();
    }

    static void Build(BuildTarget target, string locationPathName) {
        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();
        if (scenes.Length == 0)
            throw new Exception("BuildScript: no enabled scenes in Build Settings — nothing to build.");

        Directory.CreateDirectory(IOPath.GetDirectoryName(locationPathName));

        var options = new BuildPlayerOptions {
            scenes           = scenes,
            locationPathName = locationPathName,
            target           = target,
            options          = BuildOptions.None,
        };

        BuildSummary summary = BuildPipeline.BuildPlayer(options).summary;
        if (summary.result != BuildResult.Succeeded)
            throw new Exception($"BuildScript: {target} build {summary.result} with {summary.totalErrors} error(s).");

        Debug.Log($"BuildScript: {target} build succeeded -> {locationPathName} ({summary.totalSize} bytes).");
    }

    // Pull "-buildsDir <path>" out of the process args; fall back to the sibling
    // ../builds folder used for the first manual builds.
    static string BuildsDir() {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-buildsDir")
                return args[i + 1];
        return IOPath.GetFullPath(IOPath.Combine(Application.dataPath, "..", "..", "builds"));
    }
}
