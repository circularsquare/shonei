using UnityEngine;

// Renders per-slot sprite overlays on a building with FurnishingSlots. One child
// SpriteRenderer per slot, lazily populated when an item is installed; sprite resolved
// from `Item.furnishingSprite` under Resources/Sprites/Buildings/. Hidden when the slot
// is empty.
//
// Layering: each slot's renderer sits at parentSortingOrder + (1 + slotIndex) so multiple
// slots draw in a stable, predictable order above the building base sprite. Within-slot
// updates (decay-out → empty → reinstall) preserve the GameObject; we just swap the sprite.
//
// Wiring: AddComponent'd in Building.AttachAnimations() when furnishingSlots != null.
// The Building wires onSlotChanged → its own handler, which calls this.Refresh(slotIndex)
// via GetComponent. We don't subscribe to onSlotChanged directly here — Building owns
// that wire to keep the resident-walk + visual refresh in one place.
//
// If the item's furnishingSprite is null or the asset is missing, the renderer is hidden
// (renderer is created lazily — see Refresh). Slot still works mechanically.
public class FurnishingVisuals : MonoBehaviour {
    Building owner;
    int parentOrder;
    GameObject[] slotGOs;       // one per slot; null until first non-empty install
    SpriteRenderer[] slotSRs;   // parallel array; null entries match slotGOs

    public void Init(Building owner) {
        this.owner = owner;
        this.parentOrder = owner.sr != null ? owner.sr.sortingOrder : 10;
        int n = owner.furnishingSlots?.SlotCount ?? 0;
        slotGOs = new GameObject[n];
        slotSRs = new SpriteRenderer[n];
        // Refresh every slot once so saved/restored state shows up immediately on load.
        for (int i = 0; i < n; i++) Refresh(i);
    }

    public void Refresh(int slotIndex) {
        if (owner == null || owner.furnishingSlots == null) return;
        if (slotIndex < 0 || slotIndex >= slotGOs.Length) return;
        Item item = owner.furnishingSlots.Get(slotIndex);
        if (item == null || string.IsNullOrEmpty(item.furnishingSprite)) {
            // Slot empty (or item has no sprite) — hide if we'd previously spawned a renderer.
            if (slotSRs[slotIndex] != null) slotSRs[slotIndex].sprite = null;
            return;
        }
        Sprite sprite = Resources.Load<Sprite>($"Sprites/Buildings/{item.furnishingSprite}");
        if (sprite == null) {
            // Missing asset is fine — the mechanic works, the visual just doesn't render.
            // No log: many furnishings may legitimately ship without art.
            if (slotSRs[slotIndex] != null) slotSRs[slotIndex].sprite = null;
            return;
        }
        if (slotGOs[slotIndex] == null) {
            GameObject sgo = new GameObject($"furnish_{owner.furnishingSlots.slotNames[slotIndex]}");
            sgo.transform.SetParent(transform, true);
            sgo.transform.position = new Vector3(owner.x, owner.y, 0f);
            SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(sgo);
            sr.sortingOrder = parentOrder + 1 + slotIndex;
            LightReceiverUtil.SetSortBucket(sr);
            slotGOs[slotIndex] = sgo;
            slotSRs[slotIndex] = sr;
        }
        slotSRs[slotIndex].sprite = sprite;
    }
}
