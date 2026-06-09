using UnityEngine;

// Hides a toolbar button until at least one instance of a named building has been built —
// e.g. the Research button stays hidden until a laboratory exists. GetByType counts only
// built structures (blueprints under construction live in a separate list), so a placed-but-
// unfinished building does not reveal the button.
//
// Drives a CanvasGroup (alpha + raycast) rather than deactivating the GameObject, so its own
// Update keeps polling and can re-show the button. The required CanvasGroup is auto-added
// when this is attached.
//
// Scene setup: attach to the toolbar button and set Building Name (the StructType name, e.g.
// "laboratory"). No wiring needed.
[RequireComponent(typeof(CanvasGroup))]
public class BuildingBuiltGate : MonoBehaviour {
    [SerializeField] string buildingName; // StructType name that must be built before the button shows

    // Building counts only change on slow events (a build finishing, a deconstruct), so a
    // lazy poll is plenty.
    const float PollInterval = 1f;

    CanvasGroup cg;
    float       timer;

    void Awake() {
        cg = GetComponent<CanvasGroup>();
        Apply(IsBuilt());
    }

    void Update() {
        timer += Time.unscaledDeltaTime;
        if (timer < PollInterval) return;
        timer = 0f;
        Apply(IsBuilt());
    }

    bool IsBuilt() {
        if (string.IsNullOrEmpty(buildingName)) return true; // unconfigured → never hide
        if (StructController.instance == null || Db.structTypeByName == null) return false;
        if (!Db.structTypeByName.TryGetValue(buildingName, out StructType st) || st == null) {
            Debug.LogError($"BuildingBuiltGate: unknown building '{buildingName}'");
            return true; // misconfigured name shouldn't permanently hide the button
        }
        var placed = StructController.instance.GetByType(st);
        return placed != null && placed.Count > 0;
    }

    void Apply(bool shown) {
        cg.alpha          = shown ? 1f : 0f;
        cg.interactable   = shown;
        cg.blocksRaycasts = shown;
    }
}
