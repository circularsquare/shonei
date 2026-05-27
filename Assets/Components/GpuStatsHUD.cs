#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

// In-game GPU / render stats overlay. Toggle with F3.
//
// Each row shows: label  value   short interpretation
// Status colors: green = healthy, yellow = watch, red = problem.
//
// Sources:
//   - FrameTimingManager: GPU ms, CPU main thread ms. Driver-dependent on
//     some platforms — the HUD shows "--" when timings aren't available.
//   - ProfilerRecorder (new API) over built-in Render category counters:
//     SetPass, Draw, Batches, Tris, Verts. Works in builds.
//   - Sampler.Get(name).GetRecorder() (older API, gives BOTH CPU + GPU ms)
//     for per-pass markers:
//       * LightFeature passes use cmd.BeginSample/EndSample inside the
//         CommandBuffer — Unity records GPU time for these.
//       * Sky layers (CloudLayer, BackgroundLayer, HazeLayer) use plain
//         ProfilerMarker.Auto() in DoLateUpdate — CPU only (they use
//         Graphics.Blit, not CommandBuffer; GPU timing requires a larger
//         refactor to surface here).
//
// Auto-instantiated via RuntimeInitializeOnLoadMethod so no scene wiring
// needed. Compiled out of release builds entirely.
public class GpuStatsHUD : MonoBehaviour {
    public static GpuStatsHUD instance;

    // ── Toggle / display ─────────────────────────────────────────────────────
    public KeyCode toggleKey = KeyCode.F3;
    bool visible = false;

    // ── Sampling cadence ─────────────────────────────────────────────────────
    const float refreshHz = 4f;
    float nextRefreshTime;

    // ── Built-in render counters ─────────────────────────────────────────────
    ProfilerRecorder recDrawCalls;
    ProfilerRecorder recSetPass;
    ProfilerRecorder recBatches;
    ProfilerRecorder recTris;
    ProfilerRecorder recVerts;

    // ── Memory counters ──────────────────────────────────────────────────────
    ProfilerRecorder recTotalUsed;
    ProfilerRecorder recTotalReserved;
    ProfilerRecorder recGcUsed;

    // ── Per-pass markers (older Recorder API for CPU + GPU ms) ───────────────
    // Lazily resolved in Update — Sampler.Get(name) returns invalid until the
    // marker has been hit at least once.
    //
    // CPU and GPU time come from DIFFERENT samplers when both are needed:
    //   - cpuName points at a plain ProfilerMarker that wraps the C# Execute()
    //     body. That's where real CPU dispatch time lives.
    //   - gpuName points at a CustomSampler created with collectGpuData=true,
    //     used inside cmd.BeginSample/EndSample. That's the only sampler
    //     that can capture GPU shader time.
    // Reading the GPU sampler's CPU time would return ~0 (just the marker-
    // emit cost) — separate samplers, separate reads.
    class PassEntry {
        public string cpuName;       // ProfilerMarker name — always present
        public string gpuName;       // CustomSampler name — null = CPU only
        public string label;
        public bool hasGpu => gpuName != null;
        public Recorder cpuRec;
        public Recorder gpuRec;
        public float cpuMsSmoothed = -1f;
        public float gpuMsSmoothed = -1f;
    }
    PassEntry[] passes;

    // ── Frame timing (GPU/CPU ms via FrameTimingManager) ─────────────────────
    FrameTiming[] timingBuf = new FrameTiming[8];
    float gpuMsSmoothed = -1f;
    float cpuMsSmoothed = -1f;
    float fpsSmoothed   = 0f;
    bool frameTimingAvailable;

    // Cached display strings (rebuilt on refresh tick only)
    string txtFps, txtGpuMs, txtCpuMs;
    string txtSetPass, txtDraw, txtBatches, txtTris, txtVerts;
    string txtMemUsed, txtMemReserved, txtMemGc;

    Color colFps, colGpuMs, colCpuMs;
    Color colSetPass, colDraw, colBatches, colTris, colVerts;
    Color colMemUsed, colMemReserved, colMemGc;

    string hintFps, hintGpuMs, hintCpuMs;
    string hintSetPass, hintDraw, hintBatches, hintTris, hintVerts;
    string hintMemUsed, hintMemReserved, hintMemGc;

    GUIStyle styleLabel, styleValue, styleHint, styleHeader, styleBg;
    Texture2D bgTex;

    static readonly Color ok      = new(0.50f, 0.95f, 0.55f);
    static readonly Color watch   = new(1.00f, 0.85f, 0.30f);
    static readonly Color problem = new(1.00f, 0.45f, 0.45f);
    static readonly Color neutral = new(0.85f, 0.85f, 0.90f);
    static readonly Color dim     = new(0.65f, 0.65f, 0.70f);
    static readonly Color unknown = new(0.50f, 0.50f, 0.55f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoInstantiate() {
        // Defensive: domain-reload-off play mode + scene reloads can leave
        // the static `instance` ref stale (pointing to a destroyed object)
        // OR leave alive duplicates from a prior session. Search the scene
        // first, adopt one, destroy the rest. Only create if none exist.
        var existing = FindObjectsOfType<GpuStatsHUD>();
        if (existing.Length > 0) {
            instance = existing[0];
            for (int i = 1; i < existing.Length; i++) Destroy(existing[i].gameObject);
            return;
        }
        var go = new GameObject("[GpuStatsHUD]");
        go.hideFlags = HideFlags.DontSave;
        DontDestroyOnLoad(go);
        instance = go.AddComponent<GpuStatsHUD>();
    }

#if UNITY_EDITOR
    // Editor-only: wipe every HUD instance on Play-mode exit so the next
    // Play session starts with a guaranteed-clean slate. Belt-and-braces
    // with AutoInstantiate's adoption logic — if either path fails the
    // other should still produce exactly one HUD.
    [UnityEditor.InitializeOnLoadMethod]
    static void HookPlayModeExit() {
        UnityEditor.EditorApplication.playModeStateChanged += state => {
            if (state != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
            foreach (var hud in FindObjectsOfType<GpuStatsHUD>()) {
                DestroyImmediate(hud.gameObject);
            }
            instance = null;
        };
    }
#endif

    // Render-counter capacity: ~1 second at 60fps. Built-in render counters
    // tick at frame end; some Updates read between ticks and see a 0 sample.
    // We display the max across the ring so the visible number reflects
    // "real frame count" instead of flickering to zero.
    const int renderRingCapacity = 60;

    void OnEnable() {
        instance = this;
        recDrawCalls     = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count",   renderRingCapacity);
        recSetPass       = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count", renderRingCapacity);
        recBatches       = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count",       renderRingCapacity);
        recTris          = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count",     renderRingCapacity);
        recVerts         = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count",      renderRingCapacity);
        recTotalUsed     = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
        recTotalReserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Reserved Memory");
        recGcUsed        = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");

        // Marker names mirror the constants declared on each pass. Literal
        // strings here so the HUD compiles without depending on internal
        // class visibility; the constants in the pass files exist as
        // documentation that the name is HUD-load-bearing.
        passes = new[] {
            new PassEntry { cpuName = "Shonei.NormalsCapturePass", gpuName = "Shonei.NormalsCapturePass.GPU", label = "NormalsCapture" },
            new PassEntry { cpuName = "Shonei.LightPass",          gpuName = "Shonei.LightPass.GPU",          label = "LightPass"      },
            new PassEntry { cpuName = "Shonei.CloudLayer",         gpuName = null, label = "CloudLayer"      },
            new PassEntry { cpuName = "Shonei.BackgroundLayer",    gpuName = null, label = "BackgroundLayer" },
            new PassEntry { cpuName = "Shonei.HazeLayer",          gpuName = null, label = "HazeLayer"       },
        };
    }

    void OnDisable() {
        recDrawCalls.Dispose();
        recSetPass.Dispose();
        recBatches.Dispose();
        recTris.Dispose();
        recVerts.Dispose();
        recTotalUsed.Dispose();
        recTotalReserved.Dispose();
        recGcUsed.Dispose();
        if (bgTex != null) Destroy(bgTex);
    }

    void Update() {
        if (Input.GetKeyDown(toggleKey)) visible = !visible;

        // FrameTiming sample — cheap; CaptureFrameTimings is the "produce
        // the latest" call, GetLatestTimings reads from the ring.
        FrameTimingManager.CaptureFrameTimings();
        uint got = FrameTimingManager.GetLatestTimings((uint)timingBuf.Length, timingBuf);
        if (got > 0) {
            frameTimingAvailable = true;
            double gpuSum = 0, cpuSum = 0;
            for (int i = 0; i < got; i++) {
                gpuSum += timingBuf[i].gpuFrameTime;
                cpuSum += timingBuf[i].cpuFrameTime;
            }
            gpuMsSmoothed = (float)(gpuSum / got);
            cpuMsSmoothed = (float)(cpuSum / got);
        }

        // FPS — low-pass on unscaled dt so pausing doesn't NaN it.
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f) {
            float instantFps = 1f / dt;
            fpsSmoothed = (fpsSmoothed <= 0f) ? instantFps : Mathf.Lerp(fpsSmoothed, instantFps, 0.1f);
        }

        // Per-pass markers — lazy resolve (Sampler.Get returns invalid until
        // the marker has been hit at least once after Unity startup). CPU
        // and GPU come from different samplers — see PassEntry comment.
        for (int i = 0; i < passes.Length; i++) {
            var p = passes[i];
            if (p.cpuRec == null) {
                var cpuSampler = Sampler.Get(p.cpuName);
                if (cpuSampler.isValid) {
                    p.cpuRec = cpuSampler.GetRecorder();
                    p.cpuRec.enabled = true;
                }
            }
            if (p.hasGpu && p.gpuRec == null) {
                var gpuSampler = Sampler.Get(p.gpuName);
                if (gpuSampler.isValid) {
                    p.gpuRec = gpuSampler.GetRecorder();
                    p.gpuRec.enabled = true;
                }
            }
            if (p.cpuRec != null) {
                float cpuMs = p.cpuRec.elapsedNanoseconds / 1_000_000f;
                p.cpuMsSmoothed = (p.cpuMsSmoothed < 0f) ? cpuMs : Mathf.Lerp(p.cpuMsSmoothed, cpuMs, 0.15f);
            }
            if (p.gpuRec != null) {
                float gpuMs = p.gpuRec.gpuElapsedNanoseconds / 1_000_000f;
                p.gpuMsSmoothed = (p.gpuMsSmoothed < 0f) ? gpuMs : Mathf.Lerp(p.gpuMsSmoothed, gpuMs, 0.15f);
            }
        }

        if (Time.unscaledTime >= nextRefreshTime) {
            nextRefreshTime = Time.unscaledTime + 1f / refreshHz;
            RebuildStrings();
        }
    }

    void RebuildStrings() {
        // ── Frame ────────────────────────────────────────────────────────────
        txtFps = fpsSmoothed.ToString("F0");
        colFps = fpsSmoothed >= 55f ? ok : fpsSmoothed >= 30f ? watch : problem;
        hintFps = fpsSmoothed >= 55f
            ? "smooth"
            : fpsSmoothed >= 30f
                ? "playable but choppy"
                : "below 30 - investigate the heaviest row below";

        if (frameTimingAvailable && gpuMsSmoothed > 0f) {
            txtGpuMs = gpuMsSmoothed.ToString("F1") + " ms";
            colGpuMs = gpuMsSmoothed < 8f ? ok : gpuMsSmoothed < 14f ? watch : problem;
            hintGpuMs = gpuMsSmoothed < 8f
                ? "GPU has headroom"
                : gpuMsSmoothed < 14f
                    ? "approaching 60fps budget (16.6ms)"
                    : "GPU-bound - shaders, fillrate, or draw count too high";
        } else {
            txtGpuMs = "--";
            colGpuMs = unknown;
            hintGpuMs = "GPU timing unavailable on this driver";
        }

        if (frameTimingAvailable && cpuMsSmoothed > 0f) {
            txtCpuMs = cpuMsSmoothed.ToString("F1") + " ms";
            colCpuMs = cpuMsSmoothed < 8f ? ok : cpuMsSmoothed < 14f ? watch : problem;
            hintCpuMs = cpuMsSmoothed < 8f
                ? "CPU has headroom"
                : cpuMsSmoothed < 14f
                    ? "approaching 60fps budget"
                    : "CPU-bound - profile Scripts/Render categories";
        } else {
            txtCpuMs = "--";
            colCpuMs = unknown;
            hintCpuMs = "CPU timing unavailable";
        }

        // ── Render ───────────────────────────────────────────────────────────
        // Max across the ring — see renderRingCapacity comment for why max
        // and not LastValue.
        long setpass = MaxSample(recSetPass);
        long draws   = MaxSample(recDrawCalls);
        long batches = MaxSample(recBatches);
        long tris    = MaxSample(recTris);
        long verts   = MaxSample(recVerts);

        // SRP Batcher health = 1 - SetPass/Draws. The batcher doesn't reduce
        // Batches or Draws (each draw stays as its own batch); it eliminates
        // state changes between compatible consecutive draws. So fewer
        // SetPass per Draw = SRP Batcher engaging. Validated 2026-05-26;
        // sources in plans/gpu-perf-pass.md.
        float setPassRatio = draws > 0 ? (float)setpass / draws : 1f;
        float srpHealth = 1f - setPassRatio; // 0 = nothing batched, ~1 = ideal

        txtSetPass = setpass.ToString();
        colSetPass = srpHealth > 0.7f ? ok : srpHealth > 0.3f ? watch : problem;
        hintSetPass = draws == 0
            ? "no draws yet"
            : srpHealth > 0.7f
                ? $"SRP health {srpHealth * 100f:F0}% - batching well"
                : srpHealth > 0.3f
                    ? $"SRP health {srpHealth * 100f:F0}% - partial"
                    : $"SRP health {srpHealth * 100f:F0}% - atlas sprites / remove MPB";

        txtDraw = draws.ToString();
        colDraw = draws < 250 ? ok : draws < 500 ? watch : problem;
        hintDraw = draws < 250
            ? "fine"
            : draws < 500
                ? "moderate"
                : "high - profile per-camera";

        // Batches ≈ Draws is the NORMAL state under SRP Batcher (each draw
        // is still one batch). Don't treat this as a batching-health signal.
        txtBatches = batches.ToString();
        colBatches = neutral;
        hintBatches = batches == 0
            ? "no draws yet"
            : "≈Draws is normal - watch SetPass row";

        txtTris = FormatK(tris);
        colTris = tris < 100_000 ? ok : tris < 500_000 ? watch : problem;
        hintTris = tris < 100_000 ? "fine" : tris < 500_000 ? "moderate" : "high - check mesh density";

        txtVerts = FormatK(verts);
        colVerts = verts < 200_000 ? ok : verts < 800_000 ? watch : problem;
        hintVerts = verts < 200_000 ? "fine" : verts < 800_000 ? "moderate" : "high";

        // ── Memory ───────────────────────────────────────────────────────────
        long used     = recTotalUsed.LastValue;
        long reserved = recTotalReserved.LastValue;
        long gc       = recGcUsed.LastValue;
        txtMemUsed     = FormatMB(used);
        colMemUsed     = neutral;
        hintMemUsed    = "total managed + native, in-use";
        txtMemReserved = FormatMB(reserved);
        colMemReserved = dim;
        hintMemReserved = "committed to the process (used + cached)";
        txtMemGc       = FormatMB(gc);
        colMemGc       = gc < 100_000_000 ? ok : gc < 200_000_000 ? watch : problem;
        hintMemGc      = "managed heap - if it climbs every frame, look for per-frame allocations";
    }

    // Max sample across the recorder's ring. Built-in render counters
    // occasionally report 0 between frame boundaries; max-across-window
    // gives us the actual peak in the last ~second instead of those
    // spurious zero reads.
    static long MaxSample(ProfilerRecorder rec) {
        int n = rec.Count;
        if (n == 0) return rec.LastValue;
        long best = 0;
        for (int i = 0; i < n; i++) {
            long v = rec.GetSample(i).Value;
            if (v > best) best = v;
        }
        return best;
    }

    static string FormatK(long n) {
        if (n < 1000) return n.ToString();
        if (n < 1_000_000) return (n / 1000f).ToString("F1") + "k";
        return (n / 1_000_000f).ToString("F2") + "M";
    }

    static string FormatMB(long bytes) {
        if (bytes <= 0) return "--";
        return (bytes / (1024f * 1024f)).ToString("F0") + " MB";
    }

    void OnGUI() {
        if (!visible) return;
        EnsureStyles();

        const float pad = 8f;
        const float rowH = 16f;
        const float headerH = 18f;
        const float labelW = 130f;
        const float cpuW = 70f;
        const float gpuW = 70f;
        const float hintW = 360f;
        float w = pad + labelW + cpuW + gpuW + hintW + pad;

        int rows = 1 + 3   // FRAME header + 3 rows
                 + 1 + 5   // RENDER header + 5 rows
                 + 1 + passes.Length  // LIGHTING header + N pass rows + sub-header
                 + 1 + 3   // MEMORY header + 3 rows
                 + 1;      // footer
        float h = pad + (4 * headerH) + ((rows - 4) * rowH) + headerH /*column sub-header*/ + pad;

        var rect = new Rect(10, 10, w, h);
        GUI.Box(rect, GUIContent.none, styleBg);

        float y = rect.y + pad;

        DrawHeader("FRAME", rect.x + pad, y, w - pad * 2); y += headerH;
        DrawRow("FPS",      txtFps,    "",        colFps,    hintFps,    rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;
        DrawRow("GPU",      txtGpuMs,  "",        colGpuMs,  hintGpuMs,  rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;
        DrawRow("CPU",      txtCpuMs,  "",        colCpuMs,  hintCpuMs,  rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;

        DrawHeader("RENDER", rect.x + pad, y, w - pad * 2); y += headerH;
        DrawRow("SetPass",  txtSetPass, "",        colSetPass, hintSetPass, rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;
        DrawRow("Draws",    txtDraw,    "",        colDraw,    hintDraw,    rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;
        DrawRow("Batches",  txtBatches, "",        colBatches, hintBatches, rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;
        DrawRow("Tris",     txtTris,    "",        colTris,    hintTris,    rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;
        DrawRow("Verts",    txtVerts,   "",        colVerts,   hintVerts,   rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;

        DrawHeader("PASSES (per camera-frame)", rect.x + pad, y, w - pad * 2); y += headerH;
        // Sub-header naming the value columns.
        DrawColHeader(rect.x + pad, y, labelW, cpuW, gpuW); y += headerH;
        for (int i = 0; i < passes.Length; i++) {
            var p = passes[i];
            string cpu = p.cpuMsSmoothed >= 0f ? p.cpuMsSmoothed.ToString("F2") : "--";
            string gpu;
            Color col;
            string hint;
            if (p.hasGpu) {
                if (p.gpuRec == null) {
                    gpu = "--";
                    hint = "waiting for GPU marker to fire";
                    col = unknown;
                } else if (p.gpuMsSmoothed <= 0.005f) {
                    // GPU sampler is resolved but reporting effectively zero.
                    // Two common reasons:
                    //   1. Pass is genuinely cheap (<10us GPU work).
                    //   2. Driver doesn't expose GPU timestamp queries (same
                    //      driver that breaks FrameTimingManager).
                    // Show the zero honestly with a hint, not a fake number.
                    gpu = "0.00";
                    hint = "GPU timer reads zero - driver may not expose per-sample GPU time";
                    col = unknown;
                } else {
                    gpu = p.gpuMsSmoothed.ToString("F2");
                    col = p.gpuMsSmoothed < 1f ? ok : p.gpuMsSmoothed < 3f ? watch : problem;
                    hint = "GPU shader time for this pass";
                }
            } else {
                gpu = "n/a";
                col = p.cpuMsSmoothed < 1f ? ok : p.cpuMsSmoothed < 3f ? watch : problem;
                hint = "CPU dispatch only (uses Graphics.Blit)";
            }
            DrawRow(p.label, cpu, gpu, col, hint, rect.x + pad, y, labelW, cpuW, gpuW, hintW);
            y += rowH;
        }

        DrawHeader("MEMORY", rect.x + pad, y, w - pad * 2); y += headerH;
        DrawRow("Used",     txtMemUsed,     "", colMemUsed,     hintMemUsed,     rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;
        DrawRow("Reserved", txtMemReserved, "", colMemReserved, hintMemReserved, rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;
        DrawRow("GC heap",  txtMemGc,       "", colMemGc,       hintMemGc,       rect.x + pad, y, labelW, cpuW, gpuW, hintW); y += rowH;

        var footRect = new Rect(rect.x + pad, y, w - pad * 2, rowH);
        var prev = GUI.color;
        GUI.color = dim;
        GUI.Label(footRect, "F3 to hide  |  pass ms = per camera-frame, GPU is real shader time", styleHint);
        GUI.color = prev;
    }

    void DrawHeader(string label, float x, float y, float w) {
        var prev = GUI.color;
        GUI.color = new Color(0.55f, 0.75f, 1f);
        GUI.Label(new Rect(x, y, w, 18f), label, styleHeader);
        GUI.color = prev;
    }

    // Column header for the PASSES section: shows "cpu ms" and "gpu ms" labels
    // above the two value columns so users know what each number is.
    void DrawColHeader(float x, float y, float labelW, float cpuW, float gpuW) {
        var prev = GUI.color;
        GUI.color = dim;
        GUI.Label(new Rect(x + labelW,        y, cpuW, 16f), "cpu ms", styleValue);
        GUI.Label(new Rect(x + labelW + cpuW, y, gpuW, 16f), "gpu ms", styleValue);
        GUI.color = prev;
    }

    // Generic row: label | value1 | value2 | hint. value2 == "" hides the
    // second column for sections that only need one value.
    void DrawRow(string label, string value1, string value2, Color valueColor, string hint,
                 float x, float y, float labelW, float cpuW, float gpuW, float hintW) {
        var prev = GUI.color;
        GUI.color = neutral;
        GUI.Label(new Rect(x, y, labelW, 16f), label, styleLabel);
        GUI.color = valueColor;
        if (value2 == "") {
            // Single-value row — span both columns so the number aligns with
            // where users expect "the number" to be.
            GUI.Label(new Rect(x + labelW, y, cpuW + gpuW, 16f), value1, styleValue);
        } else {
            GUI.Label(new Rect(x + labelW,        y, cpuW, 16f), value1, styleValue);
            GUI.Label(new Rect(x + labelW + cpuW, y, gpuW, 16f), value2, styleValue);
        }
        GUI.color = dim;
        GUI.Label(new Rect(x + labelW + cpuW + gpuW, y, hintW, 16f), hint, styleHint);
        GUI.color = prev;
    }

    void EnsureStyles() {
        if (styleBg != null) return;

        bgTex = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave };
        bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.78f));
        bgTex.Apply();

        styleBg = new GUIStyle { normal = { background = bgTex } };
        styleLabel  = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
        styleValue  = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        styleHint   = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft };
        styleHeader = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
    }
}
#endif
