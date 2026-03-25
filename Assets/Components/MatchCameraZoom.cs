using System.Reflection;
using UnityEngine;

/// Copies the PixelPerfectCamera assetsPPU from a source camera each LateUpdate,
/// keeping the UnlitOverlayCamera's zoom in sync with the main camera.
/// Uses reflection to access assetsPPU — same workaround as MouseController
/// (direct type reference causes compile errors due to a package assembly conflict).
[RequireComponent(typeof(Camera))]
public class MatchCameraZoom : MonoBehaviour {
    [SerializeField] Camera source;

    Component     _srcPpc;
    Component     _dstPpc;
    PropertyInfo  _assetsPPU;

    void Start() {
        if (source == null) { Debug.LogError("MatchCameraZoom: source camera not assigned."); return; }

        foreach (var c in source.GetComponents<Component>()) {
            var prop = c.GetType().GetProperty("assetsPPU");
            if (prop != null) { _srcPpc = c; _assetsPPU = prop; break; }
        }
        if (_assetsPPU == null) { Debug.LogError("MatchCameraZoom: assetsPPU not found on source camera."); return; }

        foreach (var c in GetComponents<Component>()) {
            if (c.GetType() == _srcPpc.GetType()) { _dstPpc = c; break; }
        }
        if (_dstPpc == null) Debug.LogError("MatchCameraZoom: no matching PixelPerfectCamera on this GameObject.");
    }

    void LateUpdate() {
        if (_assetsPPU == null || _srcPpc == null || _dstPpc == null) return;
        // PropertyInfo is cached at Start — GetValue/SetValue here are just
        // method calls on a cached object, not reflection lookups. Negligible cost.
        _assetsPPU.SetValue(_dstPpc, _assetsPPU.GetValue(_srcPpc));
    }
}
