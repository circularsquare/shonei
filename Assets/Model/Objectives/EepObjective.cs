using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Puts the animal into the Eeping state. AnimalStateManager.HandleEeping ticks the
// sleep meter and calls task.Complete() when the mouse is fully rested.
// On Start, residents of the same home are offset by a few pixels each so multiple
// sleepers don't render exactly on top of each other.
public class EepObjective : Objective {
    // 16 PPU sprite assets → 1 game pixel = 1/16 world unit. Sleepers fan out
    // by 2 px each so multiple residents in the same housing don't pile onto
    // a single texel. Total spread for a capacity-N building = (N-1) * 0.125;
    // shack=1, burrow=2, house=4 all stay inside the leftmost interior tile.
    const float SlotPixelOffset = 2f / 16f;

    public EepObjective(Task task): base(task){}
    public override void Start(){
        animal.state = Animal.AnimalState.Eeping;
        // Slot-spread: when sleeping inside the mouse's own home, shift the
        // rendered position horizontally by (id % capacity) * 2 px so residents
        // line up rather than overlapping at the anchor. Collisions when two
        // mice share `id % capacity` are accepted — they just stack at the
        // same offset, which is the v1 baseline anyway. Gated on
        // insideBuilding == homeBuilding so a homeless mouse or a mouse
        // sleeping in a non-home building doesn't get the offset.
        Building home = animal.homeBuilding;
        if (home != null && home.interiorNodes != null && home.interiorNodes.Length > 0
            && animal.insideBuilding == home) {
            int cap = Mathf.Max(1, home.structType.capacity);
            int slot = Mathf.Abs(animal.id) % cap;
            if (slot > 0) {
                Node anchor = home.interiorNodes[0];
                // Stagger direction: walk toward the next interior tile, not a fixed +x.
                // Mirrored buildings (e.g. user's mirrored burrow) have interior nodes
                // listed in mirrored world order — interiorNodes[0] sits at the highest
                // worldX, so a fixed +x stagger would push slot-1 mice past the rightmost
                // interior tile into solid dirt. Use the sign of (next - first) so we
                // always shift toward another tile that's part of the building footprint.
                float dirX = 1f;
                if (home.interiorNodes.Length > 1) {
                    float dx = home.interiorNodes[1].wx - anchor.wx;
                    if (dx < 0f) dirX = -1f;
                    else if (dx == 0f) dirX = 1f;
                }
                animal.SnapTo(anchor.wx + slot * SlotPixelOffset * dirX, anchor.wy);
            }
        }
        // AnimalStateManager.HandleEeping calls task.Complete() when sleep finishes.
    }
}
