using UnityEngine;

// Smuggles a sprite's sortingOrder into the normal-capture shader via a
// per-renderer MaterialPropertyBlock. The normals RT's blue channel stores
// this bucket so LightCircle.shader can ramp effective light height based on
// the sort delta between a light and each receiver pixel — a receiver that
// sorts in front of a light gets lit as if the light were behind it.
//
// Usage:
//   - Call LightReceiverUtil.SetSortBucket(sr) anywhere code sets sr.sortingOrder
//     on a lit sprite.
//   - For sprites whose sortingOrder is baked into a prefab, attach the
//     LightReceiver component in the editor — it writes the MPB on Start.
//
// Because sortingOrder in Shonei is static per type, one write at spawn is
// sufficient; there is no per-frame cost. If a sprite's sortingOrder ever
// changes at runtime, call Refresh() (or SetSortBucket again) explicitly.
public static class LightReceiverUtil {
    static readonly int SortBucketId = Shader.PropertyToID("_SortBucket");
    static MaterialPropertyBlock _scratch;

    // Writes _SortBucket = sortingOrder/255 onto the renderer's MPB. We read
    // any existing MPB first so we don't stomp other properties a caller (or
    // Unity's sprite pipeline) may have set.
    public static void SetSortBucket(SpriteRenderer sr) {
        if (sr == null) { Debug.LogError("LightReceiverUtil.SetSortBucket: null SpriteRenderer"); return; }
        _scratch ??= new MaterialPropertyBlock();
        sr.GetPropertyBlock(_scratch);
        _scratch.SetFloat(SortBucketId, Mathf.Clamp01(sr.sortingOrder / 255f));
        sr.SetPropertyBlock(_scratch);
    }
}

// MonoBehaviour variant for prefabs whose sortingOrder is authored in the
// editor rather than set by code. Writes sort-bucket MPBs on Start and on
// demand via Refresh().
//
// Default behaviour: walks all SpriteRenderers in this GameObject and its
// children (so one component on an animal root handles body, feet, arm,
// clothing — each picks up its own sortingOrder). Set `target` to restrict
// to a single renderer.
[DisallowMultipleComponent]
public class LightReceiver : MonoBehaviour {
    [Tooltip("Optional single target. Leave null to apply to every SpriteRenderer in this GameObject and its children.")]
    public SpriteRenderer target;

    void Start() {
        Refresh();
    }

    // Call this if any contained SpriteRenderer's sortingOrder changes at runtime.
    public void Refresh() {
        if (target != null) {
            LightReceiverUtil.SetSortBucket(target);
            return;
        }
        var srs = GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
        if (srs.Length == 0) {
            Debug.LogError($"LightReceiver on {name}: no SpriteRenderers found to set sort buckets for.");
            return;
        }
        for (int i = 0; i < srs.Length; i++) LightReceiverUtil.SetSortBucket(srs[i]);
    }
}
