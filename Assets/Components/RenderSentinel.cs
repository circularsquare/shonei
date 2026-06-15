using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Dev-only watchdog for SILENTLY-invisible SpriteRenderers. Unity logs
// nothing when a sprite draws with no visible output (destroyed texture,
// dropped RenderTexture contents, shader with no URP-drawable pass, zero
// alpha) — failures that otherwise need the Frame Debugger to spot (cost
// us a full session post-Unity-6: see SPEC-rendering §Background layer
// footgun). This scans visible SpriteRenderers every few seconds and
// LogErrors once per (renderer, reason).
//
// What it canNOT catch: sprites buried by sorting/occlusion, lighting-
// multiplied to black, or whose content is simply off-screen — "draws but
// produces no visible pixels for GPU-side reasons" needs the Frame
// Debugger. This is a heuristic tripwire, not a proof of visibility.
//
// Bootstraps itself in the editor and development builds only (no scene
// object to maintain); release builds never run it.
public class RenderSentinel : MonoBehaviour {
    const float scanInterval = 5f;

    float nextScanTime;
    // One report per (renderer instance, reason) so a persistent failure
    // doesn't spam the console every scan.
    readonly HashSet<long> reported = new HashSet<long>();
    // Shader → has a pass the Universal renderer will draw. Shaders are
    // few and immutable at runtime; cache the verdict.
    readonly Dictionary<Shader, bool> shaderDrawable = new Dictionary<Shader, bool>();

    static readonly int MainTexId = Shader.PropertyToID("_MainTex");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap() {
        if (!Application.isEditor && !Debug.isDebugBuild) return;
        var go = new GameObject("RenderSentinel") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(go);
        go.AddComponent<RenderSentinel>();
    }

    void LateUpdate() {
        if (Time.unscaledTime < nextScanTime) return;
        nextScanTime = Time.unscaledTime + scanInterval;
        Scan();
    }

    void Scan() {
        var mpb = new MaterialPropertyBlock();
        foreach (var sr in FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None)) {
            // Only renderers the camera actually submitted — an SR that is
            // disabled or culled is invisible *legitimately*.
            if (!sr.isVisible || !sr.enabled) continue;

            // ── Effective main texture ───────────────────────────────────
            // MPB override wins over the sprite's source texture (the
            // RT-bake sky layers and chunked meshes rely on this).
            sr.GetPropertyBlock(mpb);
            Texture t = mpb.GetTexture(MainTexId);
            if ((object)t != null && t == null) {
                Report(sr, "MPB _MainTex references a DESTROYED texture (stale bind after an RT release/recreate?)");
            } else if (t is RenderTexture rt && !rt.IsCreated()) {
                Report(sr, "MPB _MainTex RenderTexture has no GPU contents (!IsCreated — lost on domain reload?)");
            } else if ((object)t == null && sr.sprite != null) {
                if (sr.sprite.texture == null) {
                    Report(sr, "sprite's source texture is destroyed/missing (atlas not packed or asset gone?)");
                } else if (SpriteRegionIsEmpty(sr.sprite)) {
                    Report(sr, $"sprite '{sr.sprite.name}' samples a 100%-transparent region of '{sr.sprite.texture.name}' " +
                               "(stale/empty atlas page? — caught the post-Unity-6 invisible-mice bug)");
                }
            }

            // ── Material / shader ────────────────────────────────────────
            // No alpha-0 tint check here: fully-transparent sr.color is a
            // legitimate state (e.g. StarField day-fades stars to a=0), so
            // it can't be distinguished from a bug — it false-positived.
            var mat = sr.sharedMaterial;
            if (mat == null) { Report(sr, "no material assigned"); continue; }
            if (!ShaderIsDrawable(mat.shader)) {
                Report(sr, $"shader '{mat.shader.name}' has no pass the Universal renderer draws " +
                           "(Universal2D-only, or compile failure) — renders nothing");
            }
        }
    }

    // Cache: sprite instanceID → its textureRect region is fully transparent.
    // GPU-readback once per unique sprite (dev-only, amortized); a visible SR
    // sampling an all-transparent region is the "stale/empty atlas page"
    // failure class (post-Unity-6 invisible mice). Sprites larger than the
    // size cap are skipped — the readback cost isn't worth it and big
    // sprites haven't exhibited this failure.
    readonly Dictionary<int, bool> spriteRegionEmpty = new Dictionary<int, bool>();
    const int regionCheckSizeCap = 512;

    bool SpriteRegionIsEmpty(Sprite sp) {
        int id = sp.GetInstanceID();
        if (spriteRegionEmpty.TryGetValue(id, out bool empty)) return empty;
        empty = false;
        Rect r;
        try { r = sp.textureRect; }  // throws for tight-packed atlas sprites; ours pack full-rect
        catch { spriteRegionEmpty[id] = false; return false; }
        var tex = sp.texture;
        if (r.width >= 1f && r.height >= 1f &&
            r.width <= regionCheckSizeCap && r.height <= regionCheckSizeCap) {
            // UV-offset Blit copies ONLY the sprite's region into a
            // rect-sized RT (sidesteps per-platform ReadPixels y-flip; we
            // only count alpha so orientation is irrelevant), then read it
            // back — works regardless of the texture's CPU Read/Write flag.
            int w = (int)r.width, h = (int)r.height;
            var tmp = RenderTexture.GetTemporary(w, h, 0,
                          RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(tex, tmp,
                          new Vector2(r.width / tex.width, r.height / tex.height),
                          new Vector2(r.x / tex.width, r.y / tex.height));
            var prev = RenderTexture.active;
            RenderTexture.active = tmp;
            var cpu = new Texture2D(w, h, TextureFormat.RGBA32, false);
            cpu.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            cpu.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            empty = true;
            foreach (var p in cpu.GetPixels32()) {
                if (p.a > 10) { empty = false; break; }
            }
            Destroy(cpu);
        }
        spriteRegionEmpty[id] = empty;
        return empty;
    }

    // A pass is drawn by URP's Universal renderer if its LightMode tag is
    // UniversalForward, SRPDefaultUnlit, or absent (untagged passes draw as
    // SRPDefaultUnlit). A Universal2D-only shader renders nothing — the trap
    // SPEC-rendering §Dual-pass sprite shaders exists to prevent.
    bool ShaderIsDrawable(Shader sh) {
        if (sh == null) return false;
        if (shaderDrawable.TryGetValue(sh, out bool ok)) return ok;
        ok = false;
        if (!sh.isSupported) {
            // compile error / unsupported — falls back, but flag it
        } else {
            var lightMode = new ShaderTagId("LightMode");
            var none      = new ShaderTagId();
            for (int i = 0; i < sh.passCount; i++) {
                var tag = sh.FindPassTagValue(i, lightMode);
                if (tag == none ||
                    tag == new ShaderTagId("UniversalForward") ||
                    tag == new ShaderTagId("SRPDefaultUnlit")) { ok = true; break; }
            }
        }
        shaderDrawable[sh] = ok;
        return ok;
    }

    // Warning, not error: these are heuristics — a hit is strong evidence
    // but legitimate states can trip them, and a dev-only watchdog shouldn't
    // paint the console red.
    void Report(SpriteRenderer sr, string reason) {
        long key = ((long)sr.GetInstanceID() << 16) ^ reason.GetHashCode();
        if (!reported.Add(key)) return;
        Debug.LogWarning($"[RenderSentinel] '{Path(sr.transform)}' is visible but likely renders nothing: {reason}", sr);
    }

    static string Path(Transform t) {
        string p = t.name;
        while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }
}
