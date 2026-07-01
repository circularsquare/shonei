using UnityEngine;

// Parks the hauler at the wellhead and hands off to the Well's bucket draw. The Well lowers/raises
// the bucket (Well.AdvanceDraw, per tick), produces the drawn water into the hauler's inventory when
// the bucket returns, and then calls this objective's Complete() to advance to the haul-off
// DropObjective. If the well or draw is torn down mid-way, the Well calls Fail() instead so the
// hauler re-plans. No per-frame logic here — the Well owns the draw clock (the bucket visual is
// interpolated separately by WellBucket).
public class DrawWaterObjective : Objective {
    private readonly Well   well;
    private readonly Recipe recipe;

    public DrawWaterObjective(Task task, Well well, Recipe recipe) : base(task) {
        this.well   = well;
        this.recipe = recipe;
    }

    public override void Start() {
        if (well == null || well.go == null) { Fail(); return; }
        // Must be at the wellhead (the preceding GoObjective just finished). Position-based, mirroring
        // WorkObjective — workNode may be an off-grid waypoint whose nearest int tile rounds away.
        Node target = well.workNode;
        bool atSpot = target != null
            && Mathf.Abs(animal.x - target.wx) < 0.5f && Mathf.Abs(animal.y - target.wy) < 0.5f;
        if (!atSpot) {
            Debug.LogError($"{animal.aName} DrawWaterObjective: not at wellhead, animal at ({animal.x},{animal.y})");
            Fail(); return;
        }
        if (!well.StartDraw(animal, this)) { Fail(); return; }
        // Stand still while the bucket works. No DrawWaterTask branch in HandleWorking, so the work
        // loop is a no-op for us — the Well drives completion. recipe set for any pose/visual reads.
        animal.recipe = recipe;
        animal.state  = Animal.AnimalState.Working;
    }
}
