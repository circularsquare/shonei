# Shonei — Books

Books are discrete, durable items that scribes write at scriptoriums and store in bookshelves. There are two kinds:

- **Tech books** — one per research tech, generated at runtime. Scientists carry one matching their current study target for a 3× research-progress multiplier.
- **Fiction book** — one hand-authored entry. Mice carry it during leisure to read it for "reading" satisfaction.

Both kinds share a single sprite (`Sprites/Items/split/books/icon`) and one shelf type. Decay rate is 2.0 (matches tools), so the same per-inv-type multipliers apply (Floor 5×, Storage/Equip 1×, Animal/Market/Blueprint 0×).

## Item class system

The books feature introduced `ItemClass` — a per-item physical category used by storage to enforce class-match acceptance. Defined in `Item.cs`:

```csharp
public enum ItemClass { Default, Liquid, Book }
```

Set on `Item.itemClass`; defaults to `Default`. Inherited by children if unset (see `Db.AddItemToDb`).

`Inventory.ItemTypeCompatible(item)` enforces:
- Storage inventories (`InvType.Storage`) always check `item.itemClass == storageClass`. Bookshelves (Book) reject non-books and vice versa.
- Non-storage inventories (Floor, Animal, Equip, etc.) accept anything **unless** they were constructed with a non-Default `storageClass`. This exception is what makes `bookSlotInv` reject non-books even though it's an Equip slot.

`StructType.storageClass` (in `buildingsDb.json`) sets the class for any storage building. Tanks use `"liquid"`, bookshelves use `"book"`, everything else defaults.

The previously-bool `Item.isLiquid` and `StructType.liquidStorage` were generalised into this enum during M1; both still exist as derived convenience properties (`item.isLiquid => itemClass == Liquid`, `structType.isLiquidStorage => storageClass == Liquid`) so liquid-rendering code in `WaterController` reads cleanly.

## Item generation

`itemsDb.json` declares the `book` group at id 300 with a single static child `fiction_book` (id 301). The group carries `decayRate: 2.0`, `discrete: true`, `itemClass: "book"` — children inherit all three.

At startup, `Db.GenerateBookItems()` (called from `Db.Awake` between `ReadJson` and the `itemsFlat` trim):

1. Looks up the `book` group.
2. Parses `researchDb.json` directly (cached in `_cachedTechs` for `GenerateBookRecipes`).
3. For each tech, creates an `Item` named `book_<techname>` at id 302+ with `itemClass = Book`, sets `parent = bookGroup`, calls `AddItemToDb`, replicates parent→child inheritance (the standard inheritance loop only fires for JSON-declared children, so runtime-attached children need it manually).
4. Appends each generated item to `bookGroup.children` so the inventory tree shows them under "book".
5. Stores the mapping in `Db.bookItemIdByTechId` (used by `ResearchTask` and `AnimalStateManager` to look up which book aids which tech).

## Recipe generation

`Db.GenerateBookRecipes()` runs immediately after `GenerateBookItems`. For each tech with a generated book item:

- Creates a `Recipe` (id 200+, skipping any occupied ids) with `job: "scribe"`, `tile: "scriptorium"`, `workload: 20`, `maxRoundsPerTask: 1` (one book per trip — see `Recipe.maxRoundsPerTask`), inputs = 1 paper, outputs = 1 matching tech book.
- Appends to the scribe job's `recipes[]` array.
- Stores the mapping in `Db.bookRecipeIdByTechId`.

The fiction book recipe is hand-authored in `recipesDb.json` (id 30) with the same `maxRoundsPerTask: 1` cap and 20-tick workload.

## Tech-gated unlocks (runtime injection)

After `Db.Awake`, `ResearchSystem.Awake` runs `LoadNodes` → `InjectBookRecipeUnlocks` → `BuildRecipeLockIndex`. The injection step appends `{type:"recipe", target:<recipeId>}` to each tech's `unlocks` array using `Db.bookRecipeIdByTechId`. So tech book recipes are gated by their own tech via the standard reverse-index pattern — no special-casing in `IsRecipeUnlocked`.

## Buildings

- **`bookshelf`** (id 25): storage building, `storageClass: "book"`, 10 stacks × 1 liang each. Auto-allows all books on construction (see `Inventory` constructor — book-class storage opts every book in by default; tanks deliberately don't auto-allow liquids since players usually dedicate a tank to one liquid). Renders as a single whole-shelf sprite via `Inventory.UpdateSprite` with three fill levels (`slow` / `smid` / `shigh`) at `Sprites/Items/split/books/`. Constructor at `Inventory.cs:84-95` excludes Book-class storage from the multi-stack-per-slot rendering branch so the whole shelf shows one sprite regardless of `nStacks`.
- **`scriptorium`** (id 107): standard workstation. `njob: "hauler"` (the *logistics* job — who builds it; operators come from `recipe.job`, see CLAUDE.md "Craft order job check" anti-pattern).

## Equip slot

`Animal.bookSlotInv` is an Equip-type inventory with `storageClass: Book` (1 stack of size 100 fen = 1 book). Saved in `AnimalSaveData.bookSlotInv` alongside the other equip slots. Restored in `Animal.Start` via `SaveSystem.LoadInventory`, which is null-safe for backward compatibility with pre-M5 saves.

## Borrow / return pattern

Two tasks fetch a book, do an activity, return the book. Both follow the same shape:

```
Fetch(book → bookSlotInv, softFetch=true)
Go(work tile)
[activity objective]
UnequipObjective(bookSlotInv)
DropObjective(book)   // delivers to a shelf, falls back to floor if all shelves full
```

The fetch is `softFetch: true` so the activity proceeds normally if the book becomes unavailable mid-task. `Cleanup` overrides on both tasks dump the book to the floor at the animal's tile if Fail leaves it stuck in `bookSlotInv` — haulers then re-shelf it.

### `ResearchTask` (M5)

Modified to optionally borrow the matching tech book. In `Initialize`, after the lab-path check:

1. Look up `Db.bookItemIdByTechId[studyTargetId]`.
2. If reachable, queue Fetch into `bookSlotInv` and the return tail.

Per-tick research progress in `AnimalStateManager.HandleWorking`:

```csharp
float researchMult = 1f;
if (Db.bookItemIdByTechId.TryGetValue(rt.studyTargetId, out int bookItemId)
    && animal.bookSlotInv.Quantity(Db.items[bookItemId]) > 0) {
    researchMult = 3f;
}
ResearchSystem.instance?.AddScientistProgress(workEfficiency * researchMult, rt.studyTargetId);
```

The multiplier applies only to research progress — `workProgress` (the study-cycle counter) is unchanged so cycle length stays consistent.

### `ReadBookTask` (M6)

Spawned by `Animal.TryPickLeisure` when a fiction book exists in the world. Picks an unoccupied standable tile within 5 of the animal (`FindNearbyReadingTile`), reads for 10 ticks. No building / seating dependency.

Per-tick happiness grant in `AnimalStateManager.HandleLeisure`:

```csharp
if (animal.task is ReadBookTask) {
    animal.happiness.NoteRead(Happiness.readingTickGrant); // 0.2/tick
}
```

Mirrors the chat per-tick pattern (0.2 social/tick × 10 ticks = 2.0 total). The `"reading"` need is registered as a hardcoded happiness need in `Db.BuildHappinessNeedRegistry` (no leisure building backs it, so the data-driven scan won't pick it up).

## Production gating

`Animal.OutputsHaveClassStorage(recipe)` — checks if every class-restricted output (Liquid, Book) has at least one matching storage with space. Used in both `PickRecipe` and `PickRecipeForBuilding` to skip recipes whose output literally has nowhere to go. Default-class outputs are exempt — they can land on the floor and be hauled later.

For books this means scribes idle when all bookshelves are full (vs piling books on the floor). For liquids it means cooks stop pressing soymilk when all tanks are full.

## Random tiebreak

`PickRecipe` and `PickRecipeForBuilding` use reservoir sampling (k=1) when multiple recipes tie at `maxScore`. Without this, iteration order (recipe id) deterministically wins ties — most visible for newly-unlocked tech books, all of which compute `Score` = +Infinity (output qty = 0 means division by zero in the `score /= qty/target` step), so the first one always wrote first.

## Future work

- **Book durability UI**: could split decay into a visible "durability" bar (1.0 → 0.0) so the player can see book wear and prioritise replacements. Currently uses the standard fen-decay system shared with food/tools.
- **Book-only haul priority**: if a hauler has a choice between hauling a book to a partially-full shelf vs filling a crate, no preference logic exists — could matter once books exist in larger quantities.
- **Tech gating for the books loop**: `bookshelf` and `scriptorium` are both `defaultLocked: false`. A "literacy" / "scribing" tech could gate the whole loop behind one unlock for game-flow reasons.
- **Animal-skill books**: out of scope; would require a third book "kind" (skill book) that takes effect via the equip path during work.
- **Multi-stack `nStacks > 4` drawer rendering**: existing rendering caps at 4 visible item sprites (`quarterOffsets.Length == 4`). Bookshelves dodge this by using single-sprite fill rendering, but actual drawers with `nStacks > 4` still hit the cap. Pre-existing — not introduced by books.
