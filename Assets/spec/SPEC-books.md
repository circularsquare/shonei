# Shonei — Books

Books are discrete, durable items that scribes write at scriptoriums and store in bookshelves. There are two kinds:

- **Tech books** — one per research tech, generated at runtime. Scientists carry one matching their current study target for a 3× research-progress multiplier.
- **Fiction book** — one hand-authored entry. Mice carry it during leisure to read it for "reading" satisfaction.

Both kinds share a single sprite (`Sprites/Items/split/books/icon`) and one shelf type. Decay rate is 0.3 (time-based, like clothing), with the standard per-inv-type multipliers (Floor 5×, Storage/Equip 1×, Animal/Market/Blueprint 0×) — so a shelved book lasts ~80 in-game days, a floored one ~16. Books are slow to write (a long tended-processor batch — see "Recipe generation") and correspondingly slow to decay.

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

`itemsDb.json` declares the `book` group at id 300 with a single static child `fiction_book` (id 301). The group carries `decayRate: 0.3`, `discrete: true`, `itemClass: "book"` — children inherit all three.

At startup, `Db.GenerateBookItems()` (called from `Db.Awake` between `ReadJson` and the `itemsFlat` trim):

1. Looks up the `book` group.
2. Parses `researchDb.json` directly (cached in `_cachedTechs` for `GenerateBookRecipes`).
3. For each tech, creates an `Item` named `book_<techname>` at id 302+ with `itemClass = Book`, sets `parent = bookGroup`, calls `AddItemToDb`, replicates parent→child inheritance (the standard inheritance loop only fires for JSON-declared children, so runtime-attached children need it manually).
4. Appends each generated item to `bookGroup.children` so the inventory tree shows them under "book".
5. Stores the mapping in `Db.bookItemIdByTechId` (used by `ResearchTask` and `AnimalStateManager` to look up which book aids which tech).

## Recipe generation

Books are **tended-processor batches**, not instant crafts — they take a long `duration` of scribe labour, stored as partial progress on the scriptorium's `Processor` so a scribe can leave to eat/sleep and the next scribe resumes the same book. This is the standard tended-processor flow (the cauldron's pattern; see `Processor.cs` and SPEC-systems §Processor). The scriptorium is therefore `hasProcessor: true`, `processorTended: true` (and stays `isWorkstation: true` for the worker-limit slider, mirroring the cauldron).

`Db.GenerateBookRecipes()` runs immediately after `GenerateBookItems`. For each tech with a generated book item:

- Creates a `Recipe` (id 200+, skipping any occupied ids) with `job: "scribe"`, `tile: "scriptorium"`, `duration: BookDuration` (a `const` in the method — the per-book labour-seconds tuning knob), `isProcessorRecipe = true`, inputs = 1 paper, outputs = 1 matching tech book.
- Buckets it into `Db.processorRecipesByBuilding["scriptorium"]` — **not** `scribe.recipes`. (The generator runs after `ReadJson`, which is what auto-buckets JSON processor recipes, so it does the bucketing manually.) Keeping book recipes out of `scribe.recipes` ensures the craft dispatch never runs them as instant `CraftTask`s — the scriptorium's `Processor` Fill/Work orders drive them. The `scribe` job ends up with an empty `recipes[]`; it exists only to operate the scriptorium processor (gated by `recipe.job` in `JobOperatesProcessor`).
- Stores the mapping in `Db.bookRecipeIdByTechId`.

The fiction book recipe is hand-authored in `recipesDb.json` (id 30) with a `duration` (no `workload`/`maxRoundsPerTask`), so `ReadJson` auto-buckets it as a scriptorium processor recipe.

## Tech-gated unlocks (runtime injection)

After `Db.Awake`, `ResearchSystem.Awake` runs `LoadNodes` → `InjectBookRecipeUnlocks` → `BuildRecipeLockIndex`. The injection step appends `{type:"recipe", target:<recipeId>}` to each tech's `unlocks` array using `Db.bookRecipeIdByTechId`. So tech book recipes are gated by their own tech via the standard reverse-index pattern — no special-casing in `IsRecipeUnlocked`.

## Buildings

- **`bookshelf`** (id 25): storage building, `storageClass: "book"`, 10 stacks × 1 liang each. Auto-allows all books on construction (see `Inventory` constructor — book-class storage opts every book in by default; tanks deliberately don't auto-allow liquids since players usually dedicate a tank to one liquid). Renders as a single whole-shelf sprite via `Inventory.UpdateSprite` with three fill levels (`slow` / `smid` / `shigh`) at `Sprites/Items/split/books/`. The `Inventory` constructor excludes Book-class storage from the multi-stack-per-slot rendering branch so the whole shelf shows one sprite regardless of `nStacks`.
- **`scriptorium`** (id 107): a **tended processor** + workstation (`hasProcessor`, `processorTended`, `isWorkstation`). `njob: "hauler"` (the *logistics* job — who builds it; operators come from `recipe.job` = scribe, see CLAUDE.md "Craft order job check" anti-pattern). Default pot capacity (one book per batch). Its `Processor.output` inventory is `ItemClass.Book` (derived from the book outputs — see `Processor` ctor); the finished book taps there, then eviction-hauls to a bookshelf. The output is created `renderless: true` so it doesn't paint a full-bookshelf sprite on the scriptorium (all processor outputs are renderless internal buffers — liquid pots draw via WaterController's zone instead).

A `ProcessorLoadVisuals` component (Components/) overlays `Sprites/Buildings/scriptorium_load` while a batch is loaded (processor state ≠ Empty), hidden when Empty. It's opt-in by art — any discrete-output processor with a `{name}_load` sprite gets it (attached from the Building ctor after the processor is created; liquid processors ship no `_load` sprite and show their batch via the pot fill instead).

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

Spawned by `Animal.TryPickLeisure` when a fiction book exists in the world. Reads for 10 ticks.

**Reading-spot selection** (in order):
1. `TryReserveBenchSeat()` — delegates to `Nav.FindPathToLeisureSeat(b => b.structType.leisureNeed == "bench" && b.CanHostLeisureNow())`, the same helper `LeisureTask.Initialize` uses. Matches on `leisureNeed` (not `structType.name`) so any future building that targets the `"bench"` satisfaction — e.g. a reading nook — participates without code changes. Reserves the seat via `Building.seatRes[i]`; released in `Cleanup()`.
2. `FindReadingTileNearShelf(shelfTile)` — fallback: iterates tiles around the **shelf** (not the animal) via `Nav.TilesAroundByDistance`, returning the first standable, unoccupied, reachable tile within `SpotSearchRadius = 5`. Anchoring on the shelf keeps the return trip short, so `DropObjective`'s 10-tile storage bonus reliably picks the shelf over a floor drop.

If seated at a bench, `Complete()` additionally grants the building's `leisureNeed` (`"bench"`) via `NoteLeisure` — so a mouse reading on a bench advances both the `"reading"` need (per-tick in `HandleLeisure`) and the `"bench"` need (on complete).

Per-tick happiness grant in `AnimalStateManager.HandleLeisure`:

```csharp
if (animal.task is ReadBookTask) {
    animal.happiness.NoteRead(Happiness.readingTickGrant); // 0.2/tick
}
```

Mirrors the chat per-tick pattern (0.2 social/tick × 10 ticks = 2.0 total). The `"reading"` need is registered as a hardcoded happiness need in `Db.BuildHappinessNeedRegistry` (no leisure building backs it, so the data-driven scan won't pick it up).

## Production gating

`Animal.OutputsHaveClassStorage(recipe)` — checks if every class-restricted output (Liquid, Book) has at least one matching storage with space. Used in `PickRecipe`/`PickRecipeForBuilding` to skip *craft* recipes whose output literally has nowhere to go. Default-class outputs are exempt — they can land on the floor and be hauled later.

Books no longer go through this gate (they're processor recipes — `PickProcessorRecipe` has no output-storage check). Instead a finished book taps into the scriptorium's own `Processor.output`, then eviction-hauls to a bookshelf. If all shelves are full the book waits in the scriptorium output, and the Fill order won't re-open until it drains — so a full-shelf colony stalls book-writing at one finished-but-unshelved book rather than idling outright. For liquids the gate still applies: cooks stop pressing soymilk when all tanks are full.

## Random tiebreak

`PickRecipe`, `PickRecipeForBuilding`, and `PickProcessorRecipe` (which books now use) all use reservoir sampling (k=1) when multiple recipes tie at `maxScore`. Without it, iteration order (recipe id) deterministically wins ties. This is most visible for newly-unlocked tech books: all compute `Score` = +Infinity (output qty = 0 divides by zero in the `score /= qty/target` step), so the first by id always wrote first.
