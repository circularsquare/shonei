using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Animal grabs a fiction book from a shelf, walks to a nearby unoccupied tile, reads it for
// ReadDuration ticks gaining "reading" leisure happiness, then returns the book to a shelf.
// Spawned by Animal.TryPickLeisure when a fiction book exists in the world. Unlike LeisureTask
// this isn't tied to a building — any standable tile near the animal that no other mouse is on
// works. Mirrors ResearchTask's borrow/return pattern (M5) for consistency.
public class ReadBookTask : Task {
    private const int ReadDuration = 10; // ticks of reading (matches ChatObjective duration)
    private const int SpotSearchRadius = 5; // tile radius around the animal to look for a free spot

    public ReadBookTask(Animal animal) : base(animal) {}

    public override bool Initialize() {
        if (!Db.itemByName.TryGetValue("fiction_book", out Item fiction)) return false;

        (Path bookPath, ItemStack bookStack) = animal.nav.FindPathItemStack(fiction);
        if (bookPath == null) return false;

        Tile readTile = FindNearbyReadingTile();
        if (readTile == null) return false;

        ReserveStack(bookStack, 100);
        objectives.AddLast(new FetchObjective(this, new ItemQuantity(fiction, 100), bookPath.tile, animal.bookSlotInv, sourceInv: bookStack.inv));
        objectives.AddLast(new GoObjective(this, readTile));
        objectives.AddLast(new LeisureObjective(this, ReadDuration));
        objectives.AddLast(new UnequipObjective(this, animal.bookSlotInv));
        objectives.AddLast(new DropObjective(this, fiction));
        return true;
    }

    // No Complete override needed: reading happiness accrues per-tick in
    // AnimalStateManager.HandleLeisure (mirrors ChatTask's per-tick social grant).

    public override void Cleanup() {
        // Mirror ResearchTask: if Fail tears the task down with the book still equipped,
        // dump it to the floor at the animal's tile so a hauler re-shelves it.
        if (animal.bookSlotInv != null && !animal.bookSlotInv.IsEmpty()) {
            var stack = animal.bookSlotInv.itemStacks[0];
            Item book = stack.item;
            int qty = stack.quantity;
            animal.Unequip(animal.bookSlotInv);
            animal.DropItem(book, qty);
        }
        base.Cleanup();
    }

    // Finds any standable tile within SpotSearchRadius of the animal that no other mouse is
    // currently on, and that the animal can path to within MediumFindRadius. No reservation —
    // if two mice race for the same spot we just accept that one ends up sharing or re-picks
    // on next tick; reading itself is harmless even with overlap.
    private Tile FindNearbyReadingTile() {
        Tile here = animal.TileHere();
        if (here == null) return null;
        World w = animal.world;
        AnimalController ac = AnimalController.instance;
        for (int dx = -SpotSearchRadius; dx <= SpotSearchRadius; dx++) {
            for (int dy = -SpotSearchRadius; dy <= SpotSearchRadius; dy++) {
                Tile t = w.GetTileAt(here.x + dx, here.y + dy);
                if (t == null || t.node == null || !t.node.standable) continue;
                if (TileHasAnimal(t, ac)) continue;
                Path p = animal.nav.PathTo(t);
                if (animal.nav.WithinRadius(p, MediumFindRadius)) return t;
            }
        }
        return null;
    }

    private static bool TileHasAnimal(Tile t, AnimalController ac) {
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a == null) continue;
            if ((int)a.x == t.x && (int)a.y == t.y) return true;
        }
        return false;
    }
}
