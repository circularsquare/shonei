using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;

// Animal grabs a fiction book from a shelf, walks to a reading spot, reads it for
// ReadDuration ticks gaining "reading" leisure happiness, then returns the book to a shelf.
// Spawned by Animal.TryPickLeisure when a fiction book exists in the world.
//
// Reading spot preference:
//   1. A reserved seat on any `bench` leisure building within MediumFindRadius (also grants
//      "bench" leisure satisfaction on Complete — same seat-reservation flow as LeisureTask).
//   2. Fallback: any standable tile within SpotSearchRadius of the shelf that no other mouse
//      is standing on. Anchoring the fallback to the shelf (not the animal) keeps the
//      return trip short enough that DropObjective's shelf preference actually wins.
//
// Mirrors ResearchTask's borrow/return pattern (M5) for the book-equip flow.
public class ReadBookTask : Task {
    private const int ReadDuration = 10; // ticks of reading (matches ChatObjective duration)
    private const int SpotSearchRadius = 5; // tile radius around the shelf to look for a free spot

    // Bench seat reservation (null / -1 when reading at a non-bench fallback spot).
    // Same shape as LeisureTask.building/seatIndex so the seat-selection contract is parallel.
    // seatBuilding is public so LeisureObjective.PoseOverride can read the seated building's
    // leisurePose (same access shape as LeisureTask.building).
    public Building seatBuilding {get; private set;}
    private int seatIndex = -1;

    public ReadBookTask(Animal animal) : base(animal) {}

    public override bool Initialize() {
        if (!Db.itemByName.TryGetValue("fiction_book", out Item fiction)) return false;

        (Path bookPath, ItemStack bookStack) = animal.nav.FindPathItemStack(fiction);
        if (bookPath == null) return false;

        // Prefer a bench seat (filters on leisureNeed not name — any future "reading nook"
        // with leisureNeed: "bench" works for free). Fall back to an unoccupied tile near
        // the shelf, so DropObjective's storage bias reliably picks the shelf on return.
        Tile readTile = TryReserveBenchSeat();
        if (readTile == null) readTile = FindReadingTileNearShelf(bookPath.tile);
        if (readTile == null) return false;

        ReserveStack(bookStack, 100);
        objectives.AddLast(new FetchObjective(this, new ItemQuantity(fiction, 100), bookPath.tile, animal.bookSlotInv, sourceInv: bookStack.inv));
        objectives.AddLast(new GoObjective(this, readTile));
        objectives.AddLast(new LeisureObjective(this, ReadDuration));
        objectives.AddLast(new UnequipObjective(this, animal.bookSlotInv));
        objectives.AddLast(new DropObjective(this, fiction));
        return true;
    }

    // Reading happiness accrues per-tick in AnimalStateManager.HandleLeisure (the
    // `animal.task is ReadBookTask` branch). On Complete we additionally grant "bench"
    // leisure if the mouse read at a bench — same pattern as LeisureTask.Complete().
    public override void Complete() {
        if (seatBuilding != null) {
            string need = seatBuilding.structType.leisureNeed;
            if (!string.IsNullOrEmpty(need)) animal.happiness.NoteLeisure(need, seatBuilding.structType.leisureGrant);
        }
        base.Complete();
    }

    public override void Cleanup() {
        // Release the bench seat before base.Cleanup so the seat is freed whether we
        // Complete or Fail.
        if (seatIndex >= 0 && seatBuilding != null && seatBuilding.seatRes != null)
            seatBuilding.seatRes[seatIndex].Unreserve();

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

    // Delegates to Nav.FindPathToLeisureSeat for the actual scan + Chebyshev-sorted first-fit.
    // Matching on leisureNeed (not structType.name) lets any future building that targets the
    // "bench" satisfaction (e.g. a reading nook) participate without further plumbing.
    private Tile TryReserveBenchSeat() {
        var (path, b, idx) = animal.nav.FindPathToLeisureSeat(
            b => b.structType.leisureNeed == "bench" && b.CanHostLeisureNow());
        if (path == null) return null;
        b.seatRes[idx].Reserve(animal.aName);
        seatBuilding = b;
        seatIndex = idx;
        return path.tile;
    }

    // Finds a standable, unoccupied tile within SpotSearchRadius of the shelf that the animal
    // can reach. Iterates by Chebyshev distance from the shelf (via Nav.TilesAroundByDistance)
    // so nearer seats are preferred — this keeps the return-to-shelf trip short enough that
    // DropObjective's storage bias picks the shelf over a floor tile. No reservation: if two
    // mice race for the same spot we just accept that one ends up sharing or re-picks next
    // tick; reading itself is harmless even with overlap.
    private Tile FindReadingTileNearShelf(Tile shelfTile) {
        if (shelfTile == null) return null;
        AnimalController ac = AnimalController.instance;
        foreach (Tile t in Nav.TilesAroundByDistance(animal.world, shelfTile.x, shelfTile.y, SpotSearchRadius)) {
            if (t == shelfTile) continue; // don't read on the shelf itself
            if (t.node == null || !t.node.standable) continue;
            if (TileHasAnimal(t, ac)) continue;
            Path p = animal.nav.PathTo(t);
            if (animal.nav.WithinRadius(p, MediumFindRadius)) return t;
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
