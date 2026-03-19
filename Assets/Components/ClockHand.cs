using UnityEngine;

// Rotates a child "clock hand" sprite to display the in-game time of day.
//
// Phase 0.0 = midnight  → hand points straight down  (−Z = 180°)
// Phase 0.25 = sunrise  → hand points left            (−Z = 90°)
// Phase 0.5  = noon     → hand points straight up     (−Z = 0°)
// Phase 0.75 = sunset   → hand points right           (−Z = 270°)
//
// Rotation is snapped to `steps` discrete positions per full rotation so that
// pixel art stays on clean pixel-grid boundaries (no sub-pixel blurriness).
// 24 steps = one position per in-game hour.
public class ClockHand : MonoBehaviour {
    [Tooltip("Discrete rotation positions per full day. 24 = one step per in-game hour.")]
    [SerializeField] int steps = 12;

    Transform handTransform;

    void Start() {
        Sprite handSprite = Resources.Load<Sprite>("Sprites/Buildings/clockhand");
        if (handSprite == null) {
            Debug.LogWarning("ClockHand: sprite not found at Resources/Sprites/Buildings/clockhand");
            return;
        }

        GameObject handGO = new GameObject("clock_hand");
        handGO.transform.SetParent(transform, false);
        handGO.transform.localPosition = Vector3.zero;

        // Point filter prevents bilinear blurring on the rotated sprite.
        // Also set this in the sprite's importer settings so it persists in builds.
        handSprite.texture.filterMode = FilterMode.Point;

        SpriteRenderer sr = handGO.AddComponent<SpriteRenderer>();
        sr.sprite = handSprite;
        // One sorting order above the clock body (Building sets sortingOrder = 10).
        sr.sortingOrder = 11;

        handTransform = handGO.transform;

        // Set initial rotation immediately so there's no one-frame lag.
        UpdateRotation();
    }

    void Update() {
        UpdateRotation();
    }

    void UpdateRotation() {
        if (handTransform == null) return;

        // Multiply by 2 for a 12-hour cycle (two full rotations per day).
        float phase = SunController.GetDayPhase() * 2f % 1f;

        // Snap to discrete steps to keep pixel art aligned.
        float snapped = Mathf.Round(phase * steps) / steps;

        // Unity Z-rotation is counter-clockwise; negate for clockwise clock motion.
        // Offset by 180° so phase=0 (midnight) points the hand straight down.
        handTransform.localEulerAngles = new Vector3(0f, 0f, 180f - snapped * 360f);
    }
}
