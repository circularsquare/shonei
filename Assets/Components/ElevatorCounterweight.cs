using UnityEngine;

// Per-frame interpolator for the elevator's counterweight child GO. Tracks the platform's
// position in reverse: when the platform sits at the bottom (currentY=0) the counterweight
// is at the top (ny-1), and vice versa. Same lerp speed as ElevatorPlatform so the visual
// stays in sync.
//
// Spawned by Elevator.AttachAnimations() on a child GameObject parented to the elevator's
// main GO and rendered behind the chassis SR; cleaned up automatically when the parent is
// destroyed.
public class ElevatorCounterweight : MonoBehaviour {
    public Elevator elevator;

    void Update() {
        if (elevator == null) return;
        int ny = elevator.Shape.ny;
        float target = (ny - 1) - elevator.currentY;
        float speed = Elevator.PlatformSpeed;
        Vector3 p = transform.localPosition;
        p.y = Mathf.MoveTowards(p.y, target, speed * Time.deltaTime);
        transform.localPosition = p;
    }
}
