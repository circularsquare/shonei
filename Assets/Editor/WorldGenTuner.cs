using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// Editor window for live-tuning WorldGen parameters. Reflects over every public
// static field on WorldGen and renders a slider (Slider for float, IntSlider
// for int, IntField for byte/other). Slider ranges are defined per-field below;
// any field not in the range table falls back to a numeric field.
//
// Persistence: every slider change writes to EditorPrefs under the key
// "Shonei.WorldGen.<FieldName>", and an [InitializeOnLoadMethod] hook on each
// editor load reads those prefs back into the WorldGen statics. This is what
// makes tuned values survive recompile / domain reload.
//
// Regenerate buttons at the top mirror the WorldGenDebugPanel's
// same-seed / new-seed flow but available from the editor window. They no-op
// when not in Play mode.
//
// Open via: Window → Shonei → WorldGen Tuner.
public class WorldGenTuner : EditorWindow {

    const string PrefPrefix = "Shonei.WorldGen.";

    // Slider ranges per field. Fields not listed get a plain numeric input —
    // safer than guessing a range. Add an entry to give a field a slider.
    struct Range { public float min, max; public Range(float a, float b){ min=a; max=b; } }
    static readonly Dictionary<string, Range> ranges = new() {
        // Terrain shape
        { "BaseHeight", new Range(20, 100) },
        { "SurfaceMin", new Range(10, 100) },
        { "SurfaceMax", new Range(20, 110) },
        { "DirtDepth", new Range(0, 10) },
        { "BedrockY", new Range(0, 10) },
        { "SurfaceFreq", new Range(0.005f, 0.3f) },
        { "SurfaceAmp", new Range(0f, 40f) },
        { "SurfaceOctaves", new Range(1, 6) },
        // Surface warp
        { "SurfaceWarpFreq", new Range(0.005f, 0.3f) },
        { "SurfaceWarpAmp", new Range(0f, 80f) },
        { "SurfaceWarpOctaves", new Range(1, 4) },
        // Amp variation
        { "SurfaceAmpVarFreq", new Range(0.001f, 0.05f) },
        { "SurfaceAmpVarMin", new Range(0f, 2f) },
        { "SurfaceAmpVarMax", new Range(0f, 3f) },
        // Spawn zone
        { "SpawnMinX", new Range(0, 200) },
        { "SpawnMaxX", new Range(0, 200) },
        { "SpawnBlend", new Range(0, 16) },
        // Veins
        { "GraniteFreq", new Range(0.01f, 0.3f) },
        { "GraniteThreshold", new Range(0f, 1f) },
        { "GraniteDepthCenter", new Range(0f, 1f) },
        { "GraniteDepthWidth", new Range(0.01f, 1f) },
        { "SlateFreq", new Range(0.01f, 0.3f) },
        { "SlateThreshold", new Range(0f, 1f) },
        { "SlateDepthCenter", new Range(0f, 1f) },
        { "SlateDepthWidth", new Range(0.01f, 1f) },
        // Caves
        { "CaveFreqX", new Range(0.01f, 0.3f) },
        { "CaveFreqY", new Range(0.01f, 0.5f) },
        { "CaveOctaves", new Range(1, 5) },
        { "CavePersistence", new Range(0.1f, 1f) },
        { "CaveLacunarity", new Range(1f, 4f) },
        { "CaveThresholdSurface", new Range(0f, 1f) },
        { "CaveThresholdDeep", new Range(0f, 1f) },
        { "CaveExclusionBelow", new Range(0, 20) },
        { "CACycles", new Range(0, 6) },
        { "MinCaveSize", new Range(1, 30) },
        // Worms
        { "WormCountMin", new Range(0, 6) },
        { "WormCountMax", new Range(0, 6) },
        { "WormMinSeparation", new Range(0, 80) },
        { "WormSpawnBuffer", new Range(0, 10) },
        { "WormMinSteps", new Range(10, 400) },
        { "WormMaxSteps", new Range(10, 400) },
        { "WormRadius", new Range(0, 4) },
        { "WormFalloff", new Range(0, 4) },
        { "ChamberRadius", new Range(0, 5) },
        { "ChamberInterval", new Range(1, 10000) },
        { "WormStrength", new Range(0f, 1f) },
        { "WormTurnChance", new Range(0f, 1f) },
        // Water
        { "WaterLine", new Range(20, 100) },
        { "WaterChunkCount", new Range(1, 8) },
        { "WaterChunkBudgetMin", new Range(0, 200) },
        { "WaterChunkBudgetMax", new Range(0, 300) },
        { "MinPoolVolume", new Range(1, 30) },
        // Beach + plants + moisture
        { "SandFreq", new Range(0.01f, 0.5f) },
        { "SandThreshold", new Range(0f, 1f) },
        { "PlantChance", new Range(0f, 0.5f) },
        { "ClusterMin", new Range(1, 6) },
        { "ClusterMax", new Range(1, 8) },
        { "StartingMoisture", new Range(0, 255) },
    };

    [MenuItem("Window/Shonei/WorldGen Tuner")]
    public static void Open() {
        GetWindow<WorldGenTuner>("WorldGen Tuner");
    }

    Vector2 scroll;

    // Source-file defaults captured before any EditorPrefs override is applied.
    // Used to show the "modified vs file" indicator and to power Reset/Save.
    // Refreshed by [InitializeOnLoadMethod] (each recompile) and by SaveToFile.
    static readonly Dictionary<string, object> sourceDefaults = new();

    const string WorldGenSourcePath = "Assets/Model/WorldGen.cs";

    [InitializeOnLoadMethod]
    static void RestoreFromPrefs() {
        // Snapshot first — fields currently hold their source-file initialiser
        // values. Then overlay EditorPrefs on top.
        sourceDefaults.Clear();
        foreach (FieldInfo f in TunableFields()) {
            sourceDefaults[f.Name] = f.GetValue(null);
        }
        foreach (FieldInfo f in TunableFields()) {
            string key = PrefPrefix + f.Name;
            if (!EditorPrefs.HasKey(key)) continue;
            try {
                if (f.FieldType == typeof(int))   f.SetValue(null, EditorPrefs.GetInt(key));
                else if (f.FieldType == typeof(float)) f.SetValue(null, EditorPrefs.GetFloat(key));
                else if (f.FieldType == typeof(byte))  f.SetValue(null, (byte)Mathf.Clamp(EditorPrefs.GetInt(key), 0, 255));
            } catch (Exception e) {
                Debug.LogWarning($"WorldGenTuner: failed to restore {f.Name}: {e.Message}");
            }
        }
    }

    static bool IsModified(FieldInfo f) {
        if (!sourceDefaults.TryGetValue(f.Name, out object src)) return false;
        object cur = f.GetValue(null);
        if (f.FieldType == typeof(float)) return !Mathf.Approximately((float)cur, (float)src);
        return !Equals(cur, src);
    }

    static IEnumerable<FieldInfo> TunableFields() {
        return typeof(WorldGen).GetFields(BindingFlags.Public | BindingFlags.Static);
    }

    void OnGUI() {
        EditorGUILayout.LabelField("Regenerate", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(!Application.isPlaying)) {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Same seed")) RegenerateSameSeed();
            if (GUILayout.Button("New seed"))  RegenerateNewSeed();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"Last seed: {WorldController.lastGeneratedSeed}");
        }
        if (!Application.isPlaying)
            EditorGUILayout.HelpBox("Regenerate buttons require Play mode.", MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Save current settings"))   SaveToFile();
        if (GUILayout.Button("Restore defaults"))     ResetLiveToFile();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        scroll = EditorGUILayout.BeginScrollView(scroll);

        foreach (FieldInfo f in TunableFields()) {
            DrawField(f);
        }

        EditorGUILayout.EndScrollView();
    }

    void DrawField(FieldInfo f) {
        string key = PrefPrefix + f.Name;
        bool hasRange = ranges.TryGetValue(f.Name, out Range r);

        // Two-space pad on unmodified keeps labels aligned with modified rows
        // (which get a "* " prefix). The trailing space after * keeps the
        // field name from butting against the dot.
        bool modified = IsModified(f);
        string label = (modified ? "* " : "  ") + f.Name;

        Color prev = GUI.color;
        if (modified) GUI.color = new Color(1f, 0.85f, 0.4f); // amber-ish tint for modified rows

        if (f.FieldType == typeof(float)) {
            float v = (float)f.GetValue(null);
            float nv = hasRange
                ? EditorGUILayout.Slider(label, v, r.min, r.max)
                : EditorGUILayout.FloatField(label, v);
            if (!Mathf.Approximately(nv, v)) {
                f.SetValue(null, nv);
                EditorPrefs.SetFloat(key, nv);
            }
        } else if (f.FieldType == typeof(int)) {
            int v = (int)f.GetValue(null);
            int nv = hasRange
                ? EditorGUILayout.IntSlider(label, v, (int)r.min, (int)r.max)
                : EditorGUILayout.IntField(label, v);
            if (nv != v) {
                f.SetValue(null, nv);
                EditorPrefs.SetInt(key, nv);
            }
        } else if (f.FieldType == typeof(byte)) {
            int v = (byte)f.GetValue(null);
            int nv = hasRange
                ? EditorGUILayout.IntSlider(label, v, (int)r.min, (int)r.max)
                : EditorGUILayout.IntField(label, v);
            nv = Mathf.Clamp(nv, 0, 255);
            if (nv != v) {
                f.SetValue(null, (byte)nv);
                EditorPrefs.SetInt(key, nv);
            }
        }

        GUI.color = prev;
        // Other types (string, bool) are not tunable here — skip silently.
    }

    // Restores live field values to the captured source-file defaults and
    // clears any EditorPrefs overrides. Immediate; no recompile needed.
    void ResetLiveToFile() {
        foreach (FieldInfo f in TunableFields()) {
            if (sourceDefaults.TryGetValue(f.Name, out object src)) {
                f.SetValue(null, src);
            }
            EditorPrefs.DeleteKey(PrefPrefix + f.Name);
        }
        Repaint();
    }

    // Writes current live values back into WorldGen.cs as the new file defaults,
    // then clears EditorPrefs and refreshes the source-defaults snapshot. After
    // this, modified-indicators reset (current == new file default) and the
    // file becomes canonical.
    void SaveToFile() {
        if (!File.Exists(WorldGenSourcePath)) {
            EditorUtility.DisplayDialog("WorldGen Tuner", $"File not found: {WorldGenSourcePath}", "OK");
            return;
        }
        if (Application.isPlaying) {
            // Writing the source triggers AssetDatabase.Refresh → recompile →
            // exit play. Let the user back out if they have unsaved play state.
            if (!EditorUtility.DisplayDialog("WorldGen Tuner",
                    "Saving will recompile and exit Play mode. Continue?", "Save", "Cancel"))
                return;
        }
        string text = File.ReadAllText(WorldGenSourcePath);
        int changes = 0;
        var notFound = new List<string>();
        foreach (FieldInfo f in TunableFields()) {
            string lit = FormatLiteral(f.GetValue(null), f.FieldType);
            if (lit == null) continue;

            // Match `public static <type> Name = <value>;` preserving trailing
            // comment / whitespace. Value section can't contain `;` so a non-
            // greedy match terminated on `;` is safe.
            string pat = $@"(\bpublic\s+static\s+(?:int|float|byte)\s+{Regex.Escape(f.Name)}\s*=\s*)([^;\r\n]+?)(\s*;)";
            Regex re = new Regex(pat);
            Match m = re.Match(text);
            if (!m.Success) { notFound.Add(f.Name); continue; }
            if (m.Groups[2].Value.Trim() == lit) continue; // unchanged
            text = re.Replace(text, m.Groups[1].Value + lit + m.Groups[3].Value, 1);
            changes++;
        }
        if (changes > 0) {
            File.WriteAllText(WorldGenSourcePath, text);
            AssetDatabase.Refresh();
        }

        // Refresh snapshot so the modified-indicators reset. EditorPrefs is now
        // stale (file is canonical) — clear it. The upcoming recompile will
        // re-run RestoreFromPrefs which will re-snapshot from the new file
        // values; doing it here too keeps the UI correct until then.
        sourceDefaults.Clear();
        foreach (FieldInfo f in TunableFields()) {
            sourceDefaults[f.Name] = f.GetValue(null);
            EditorPrefs.DeleteKey(PrefPrefix + f.Name);
        }

        string msg = $"Saved {changes} value(s) to {WorldGenSourcePath}. EditorPrefs cleared.";
        if (notFound.Count > 0) msg += $"\n\nCouldn't locate declarations for: {string.Join(", ", notFound)}";
        EditorUtility.DisplayDialog("WorldGen Tuner", msg, "OK");
    }

    // Formats a value as a C# literal matching the style WorldGen.cs uses.
    static string FormatLiteral(object v, Type t) {
        if (t == typeof(int))   return ((int)v).ToString(CultureInfo.InvariantCulture);
        if (t == typeof(byte))  return ((byte)v).ToString(CultureInfo.InvariantCulture);
        if (t == typeof(float)) {
            // "0.0######" guarantees at least one decimal place (so `10` round-trips
            // as `10.0f`) and trims trailing zeros (so 0.06 stays `0.06f`, not
            // `0.060000f`). Suffix `f` makes it a float literal.
            return ((float)v).ToString("0.0######", CultureInfo.InvariantCulture) + "f";
        }
        return null;
    }

    void RegenerateSameSeed() {
        if (SaveSystem.instance == null) return;
        WorldController.pendingSeedOverride = WorldController.lastGeneratedSeed != 0
            ? WorldController.lastGeneratedSeed
            : (int?)null;
        SaveSystem.instance.LoadDefault();
    }

    void RegenerateNewSeed() {
        if (SaveSystem.instance == null) return;
        WorldController.pendingSeedOverride = UnityEngine.Random.Range(1, 100000);
        SaveSystem.instance.LoadDefault();
    }
}
