using UnityEngine;

// Shared input helpers for UI +/- stepper buttons. Ctrl-click steps by 10× the
// normal amount (jobs ±10, inventory targets ±10 liang, …) for fast bulk edits.
// Uses the legacy Input API to match the rest of the project's keyboard handling.
public static class UIInput {
    // The bulk-edit multiplier for a +/- button: 10× when either Ctrl is held at
    // click time, else 1×. Multiply a button's normal step by this — and read it
    // INSIDE the click handler (not when wiring the listener) so it reflects the
    // modifier state at the moment of the click.
    public static int StepMultiplier =>
        (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) ? 10 : 1;
}
