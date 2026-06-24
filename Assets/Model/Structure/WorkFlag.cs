using UnityEngine;

// A cosmetic work-order marker. Mice are assigned to work/rest at the flag's tile (see
// Animal.assignedFlag); gameplay gates on the structType.isWorkFlag data flag, not on this
// class. The only thing the subclass adds is the wind-reactive cloth animation.
//
// The static building sprite (workflag.png) renders for the build menu / ghost; once placed,
// WorkFlagVisuals takes over the SpriteRenderer with the first frame of the workflag_wind
// sheet, flipping it to face downwind. If that sheet is missing or unsliced, the static
// sprite just stays.
public class WorkFlag : Building {
    public WorkFlag(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    public override void AttachAnimations() {
        base.AttachAnimations();
        Sprite[] frames = Resources.LoadAll<Sprite>("Sprites/Buildings/workflag_wind");
        if (frames == null || frames.Length <= 1) return;  // unsliced → keep the static sprite
        WorkFlagVisuals vis = go.AddComponent<WorkFlagVisuals>();
        vis.frames = frames;
    }
}
