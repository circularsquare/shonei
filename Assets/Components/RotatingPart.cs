using System;
using UnityEngine;

// Generic per-frame rotator for a child GameObject — spins via transform.localRotation
// at a speed sourced from gameplay state (wind, charge fraction, etc.). Holds the current
// rotation when isActive is false (no neutral pose required), so a stopping wheel reads as
// "no longer driven" rather than snapping back to a default angle.
//
// Used by power buildings whose visual has a rotating part: windmill blades sourced from
// WeatherSystem.wind, flywheel inner wheel sourced from charge/Capacity, etc. Each building
// authors a centred-pivot `<name>_wheel.png` (or similar) for the spinning child sprite,
// and configures one of these on the child GameObject from its Structure.AttachAnimations()
// override.
public class RotatingPart : MonoBehaviour {
    // Returns a signed scalar in roughly [-1, 1]. Sign drives spin direction (positive =
    // counter-clockwise in world space at directionSign=1); magnitude scales speed.
    public Func<float> speedSource;

    // Optional gate. When this returns false, rotation pauses and the current angle is held.
    // Null means "always active".
    public Func<bool>  isActive;

    public float       degPerSecAtMaxSpeed = 180f;
    public float       stallThreshold      = 0.05f;
    public float       directionSign       = 1f;

    void Update() {
        if (isActive != null && !isActive()) return;
        float s = speedSource != null ? speedSource() : 0f;
        if (Mathf.Abs(s) < stallThreshold) return;
        // Clamp so a brief spike past 1.0 doesn't visibly accelerate beyond design speed.
        float clamped = Mathf.Clamp(s, -1f, 1f);
        float deg = clamped * degPerSecAtMaxSpeed * directionSign * Time.deltaTime;
        transform.localRotation *= Quaternion.Euler(0f, 0f, deg);
    }
}
