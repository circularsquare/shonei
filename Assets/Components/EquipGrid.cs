using System.Collections.Generic;
using UnityEngine;

// ── EquipGrid ──────────────────────────────────────────────────────────────
// Reusable RPG-style gear grid for an animal's equip slots (food/hat/book/top/tool).
// Attach to a GridLayoutGroup container (3 columns, fixed cell size). On the first Bind
// it instantiates one EquipSlotDisplay box per slot into itself, then binds them to the
// bound animal's equip inventories and repaints on Refresh. Both the InfoPanel quick view
// (AnimalInfoView) and the population panel detail pane (MouseDetailView) own one of these,
// so the gear readout is identical in both places.
public class EquipGrid : MonoBehaviour {
    [SerializeField] EquipSlotDisplay slotPrefab; // gear-box prefab (frame + inner icon)

    // Which equip inventory a grid cell maps to. Blank = a reserved empty cell, kept so the
    // grid can grow into a new slot later without reshuffling the layout.
    enum Kind { Blank, Food, Hat, Book, Top, Tool }

    // Fixed grid layout, row-major in the 3-column grid:
    //   food  hat   book
    //   ( )   top   tool
    // The set is fixed (one cell per Animal equip inventory) so it lives in code, not the
    // inspector; empty-slot outline sprites load by name from the folder below.
    static readonly (Kind kind, string label, string sprite)[] layout = {
        (Kind.Food,  "food", "foodequip"),
        (Kind.Hat,   "hat",  "hatequip"),
        (Kind.Book,  "book", "bookequip"),
        (Kind.Blank, null,   null),
        (Kind.Top,   "top",  "topequip"),
        (Kind.Tool,  "tool", "toolequip"),
    };
    const string spriteDir = "Sprites/Misc/equipslots/"; // Resources-relative

    List<EquipSlotDisplay> slots;
    readonly Dictionary<string, Sprite> emptySprites = new Dictionary<string, Sprite>();

    // Bind every box to the given animal's equip inventories (builds the boxes on first call).
    public void Bind(Animal a) {
        BuildIfNeeded();
        if (slots == null || a == null) return;
        for (int i = 0; i < slots.Count && i < layout.Length; i++) {
            var def = layout[i];
            slots[i].SetSlot(def.label, EmptySprite(def.sprite), SlotInv(def.kind, a), ShowsCondition(def.kind));
        }
    }

    // Repaint each box from its current stack — picks up live wear/equip changes without a rebind.
    public void Refresh() {
        if (slots == null) return;
        foreach (var s in slots) if (s != null) s.Refresh();
    }

    void BuildIfNeeded() {
        if (slots != null) return;
        slots = new List<EquipSlotDisplay>();
        if (slotPrefab == null) {
            Debug.LogError("EquipGrid: slotPrefab not assigned — gear grid disabled.");
            return;
        }
        foreach (var _ in layout) {
            var d = Instantiate(slotPrefab, transform);
            d.gameObject.SetActive(true);
            slots.Add(d);
        }
    }

    // Maps a grid cell to the animal's backing equip inventory (null for a Blank cell).
    static Inventory SlotInv(Kind kind, Animal a) {
        switch (kind) {
            case Kind.Food: return a.foodSlotInv;
            case Kind.Hat:  return a.hatSlotInv;
            case Kind.Book: return a.bookSlotInv;
            case Kind.Top:  return a.clothingSlotInv;
            case Kind.Tool: return a.toolSlotInv;
            default:        return null;
        }
    }

    // Worn gear shows a durability %; food shows amount instead (it's consumed, not worn).
    static bool ShowsCondition(Kind kind) =>
        kind == Kind.Tool || kind == Kind.Top || kind == Kind.Hat || kind == Kind.Book;

    // Loads and caches an empty-slot outline sprite by file name (under spriteDir).
    Sprite EmptySprite(string name) {
        if (string.IsNullOrEmpty(name)) return null;
        if (emptySprites.TryGetValue(name, out Sprite s)) return s;
        s = Resources.Load<Sprite>(spriteDir + name);
        if (s == null) Debug.LogError($"EquipGrid: missing equip-slot sprite Resources/{spriteDir}{name}");
        emptySprites[name] = s;
        return s;
    }
}
