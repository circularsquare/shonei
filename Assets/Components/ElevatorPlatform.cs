using UnityEngine;

// Per-frame interpolator for the elevator's platform child GO. Reads the elevator's
// currentY (mutated discretely each tick by Elevator.Tick) and lerps localPosition.y
// toward it at PlatformSpeed tiles/second so the platform glides between tile rows
// instead of snapping. Also drags the loaded passenger along — pinning their world
// position to (elevator.x, elevator.y + platformLocalY) — so the rider moves smoothly
// with the visual platform instead of teleporting per tick.
//
// Spawned by Elevator.AttachAnimations() on a child GameObject parented to the elevator's
// main GO; cleaned up automatically when the parent is destroyed.
public class ElevatorPlatform : MonoBehaviour {
    public Elevator elevator;

    void Update() {
        if (elevator == null) return;
        // Platform sprite sits one tile below the passenger so it visually supports them.
        // The lerp target is therefore (currentY − 1); the passenger drag below adds the
        // tile back so the rider lands at elevator.y + currentY.
        float target = elevator.currentY - 1f;
        // PlatformSpeed is tiles/tick and one tick = one in-game second, so this lerp
        // catches up to the discrete target by the next tick at 1× game speed. If the
        // game ever introduces a speed multiplier, this will lag — fix it then.
        float speed = Elevator.PlatformSpeed;
        Vector3 p = transform.localPosition;
        p.y = Mathf.MoveTowards(p.y, target, speed * Time.deltaTime);
        transform.localPosition = p;

        // Drag the passenger along with the platform's smooth visual position rather than
        // the discrete tick-stepped currentY — keeps the mouse from teleporting between
        // tile rows mid-trip. +1 here is the inverse of the platform's -1 offset.
        Animal pass = elevator.passenger;
        if (pass != null && pass.go != null) {
            pass.x = elevator.x;
            pass.y = elevator.y + p.y + 1f;
            pass.go.transform.position = new Vector3(pass.x, pass.y, pass.z);
        }
    }
}
