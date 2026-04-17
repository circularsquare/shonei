using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

public class ResearchTask : Task {
    private readonly Building _lab;
    // Which tech this scientist is working on, chosen by PickStudyTarget at task creation.
    // -1 means nothing to do (scientist will idle through the work loop with no effect).
    public int studyTargetId = -1;
    public ResearchTask(Animal animal, Building lab) : base(animal) { _lab = lab; }
    public override bool Initialize() {
        if (_lab == null) return false;
        Path p = animal.nav.PathToOrAdjacent(_lab.tile);
        if (!animal.nav.WithinRadius(p, MediumFindRadius)) return false;

        // Optional book borrow: if a matching book exists somewhere reachable, queue a
        // fetch into bookSlotInv before the research, and a return (unequip + drop to
        // shelf) after. The 3× progress multiplier is applied later in HandleWorking
        // based on bookSlotInv contents — this method just sets up the fetch/return tail.
        // softFetch on the FetchObjective: if the book becomes unavailable between this
        // check and execution (e.g. another scientist grabs it), the scientist proceeds
        // to research without the multiplier instead of failing the whole task.
        Item book = null;
        if (Db.bookItemIdByTechId.TryGetValue(studyTargetId, out int bookItemId)) {
            Item candidate = Db.items[bookItemId];
            (Path bookPath, ItemStack bookStack) = animal.nav.FindPathItemStack(candidate);
            if (bookPath != null) {
                book = candidate;
                ReserveStack(bookStack, 100);
                objectives.AddLast(new FetchObjective(this, new ItemQuantity(book, 100), bookPath.tile, animal.bookSlotInv, softFetch: true, sourceInv: bookStack.inv));
            }
        }

        objectives.AddLast(new GoObjective(this, p.tile));
        objectives.AddLast(new ResearchObjective(this));

        if (book != null) {
            // After research: move book from slot to main inv, then deliver to a shelf
            // (DropObjective finds an accepting storage inv — books only fit in bookshelves).
            // If shelves are full, DropObjective falls back to the floor; haulers will then
            // move it to a shelf when one frees up.
            objectives.AddLast(new UnequipObjective(this, animal.bookSlotInv));
            objectives.AddLast(new DropObjective(this, book));
        }
        return true;
    }

    // If the task ends with a book still equipped (Fail mid-flight before the return objectives
    // ran), dump it from the slot to the animal's tile so it's not orphaned in the equip slot.
    // Floor drop creates a haul order; haulers will move it to a shelf eventually.
    public override void Cleanup() {
        if (animal.bookSlotInv != null && !animal.bookSlotInv.IsEmpty()) {
            var stack = animal.bookSlotInv.itemStacks[0];
            Item book = stack.item;
            int qty = stack.quantity;
            animal.Unequip(animal.bookSlotInv);
            animal.DropItem(book, qty);
        }
        base.Cleanup();
    }
}
