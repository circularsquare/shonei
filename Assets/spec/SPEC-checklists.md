# Shonei — Content Authoring Checklists

Quick-reference checklists for adding new content. These cover the *workflow* and *footguns*; field-by-field schemas live in [SPEC-data.md](SPEC-data.md).

If you're adding a building, recipe, item, job, plant, research node, exclusive UI panel, sprite, or `Structure` subclass — read the relevant section below before editing JSON.

> For new Tasks, Objectives, or WOM order types, follow the 11-step checklist in [SPEC-ai.md](SPEC-ai.md) instead — this doc doesn't duplicate it.

---

## Adding a new building (`buildingsDb.json`)

- [ ] **`njob` is the LOGISTICS job — who hauls and constructs, not who operates.** Omit it (defaults to `"hauler"`) unless construction itself needs a specialist. The operator of a workstation is determined per-recipe via `recipe.job`. — Setting `njob: "cook"` on a kitchen means *cooks build the kitchen* and no one else can — that's why no one shows up to construct it.
- [ ] **`id` falls in the right range** for its category (see ID-range table in SPEC-data.md). Keep entries ordered.
- [ ] **Costs in liang** (float), not fen. `{ "name": "wood", "quantity": 3 }` = 3 liang.
- [ ] **Group vs leaf item in costs.** `"wood"` is a wildcard accepting any wood-type leaf; `"oak planks"` is a specific leaf. Pick deliberately. (Group items are never physical — see SPEC-data.md.)
- [ ] **`category` must be `"storage"` / `"structures"` / `"tiles"` / `"production"`** — any other value silently hides the building from the build menu.
- [ ] **`depth`, `solidTop`** copied from a similar building rather than guessed.
- [ ] **Placement constraints** go in `tileRequirements`, not freeform fields. (Water under windmill, dirt-bank for burrow, etc.)
- [ ] **`requiredTileName` matches tile name OR group** (e.g. `"stone"` accepts limestone, granite, slate via `TileType.group`).
- [ ] **Paired-field gotchas**: `isHousing` needs `capacity`; `isDecoration` needs `decorationNeed`; `isLeisure` needs `leisureNeed`. Missing the partner = building works but grants no slots / happiness.
- [ ] **If it's a `Structure` subclass** (Windmill, Quarry, …): see "Adding a new Structure subclass" below.
- [ ] **If it has recipes**: see "Adding a new recipe" below — `recipe.tile` must exactly match this building's `name`.

## Adding a new recipe (`recipesDb.json`)

- [ ] **`tile` matches a building's `name` exactly.** Typo → recipe is orphaned and silently never runs.
- [ ] **`job` field is the OPERATOR job** — the animal job that picks up this recipe at the building. (Distinct from the building's `njob`.)
- [ ] **`workload` is in TICKS (1 tick ≈ 1 second), not seconds.** No unit suffix in the field name; easy to author at 60× intended.
- [ ] **Inputs/outputs in liang** (float).
- [ ] **All input/output item names exist in `itemsDb.json`.** Typo → silent orphan at load.
- [ ] **Group input = wildcard accept.** First delivery locks the blueprint to that specific leaf (`LockGroupCostsAfterDelivery`).
- [ ] **No group items in `noutputs`** — outputs must be leaves. `Db.ValidateNoGroupOutputs()` logs at startup but the error is easy to miss in console spam.

## Adding a new item (`itemsDb.json`)

- [ ] **Liang in JSON, fen in code.** Conversion (`*100`) happens at the JSON-to-runtime boundary.
- [ ] **Group items are never physical.** If this item should sit in an inventory, it's a leaf. If it's a category for recipe wildcards, it's a group. Decide.
- [ ] **`defaultTarget` defaults to 100 liang.** Byproducts (sawdust, acorn, pinecone) want `10` — otherwise multi-product harvest gating won't fire.
- [ ] **Icon sprite exists** — see "Adding a new item sprite" below.
- [ ] **`itemClass` set** if storage restrictions apply (liquid → tank, book → bookshelf).
- [ ] **New `happinessNeed`?** Also add the need name to `Db.happinessNeedsDisplayOrder` ([Db.cs:48](../Model/Db.cs#L48)) — otherwise the row sorts alphabetically at the bottom of UI panels.

## Adding a new job (`jobsDb.json`)

- [ ] **`recipes` array lists every recipe this job can operate.** This is the actual eligibility gate — animals only see recipes their job allows.
- [ ] **`defaultSkill` propagates to recipes that don't set their own `skill`** ([Db.cs:512](../Model/Db.cs#L512)). Picking the wrong default silently mislabels XP for every recipe under this job.
- [ ] **`defaultLocked: true`** needs an unlock entry on some research node, or the job stays hidden forever.
- [ ] Animals can be assigned to this job somewhere in UI, or it's dead.

## Adding a new plant (`plantsDb.json`)

- [ ] **`njob` here is overloaded — it means the HARVEST job**, not logistics. (Different from buildings.)
- [ ] **`genWeight: 0` (default) = never spawns naturally.** Set positive for wild plants; leave 0 for crops planted only by the player.
- [ ] **`tempMin` / `moistureMin`: `null` ≠ `0`.** `null` means "no lower bound"; `0` is a real constraint. Watch the distinction.
- [ ] Growth stages and yields in liang.
- [ ] **No group items in `nproducts`** — products must be leaves.

## Adding a new research node (`researchDb.json`)

- [ ] **`id` is unique** and `prereqs` reference real prior ids.
- [ ] **`unlocks` targets match real names** (building / recipe / job). Typos silently leave content locked or un-gated.
- [ ] **Unlocking a `defaultLocked` job/building?** This entry is the only path that surfaces it — double-check the spelling.
- [ ] **Icon at `Sprites/Researches/<name>`** or it falls back silently.

## Adding a new `Structure` subclass (e.g. new Building/Plant variant)

- [ ] **Add dispatch case to `Structure.Create()`** in [Structure.cs](../Model/Structure/Structure.cs). Without it, your StructType falls back to plain `Building` and overrides never fire.
- [ ] **File goes in `Assets/Model/Structure/`** — not `Assets/Components/`. (See CLAUDE.md folder conventions.)
- [ ] **If `AttachAnimations` is overridden**: don't reference subclass-side fields from it — it runs during `base()` before subclass ctor body.
- [ ] **Any new `dx` field** (interior tile, door, ladder, workSpot, furnishing offset, …): apply `nx-1-dx` on mirror lookup. Convention used throughout — see [StructType.cs:45-66](../Model/Structure/StructType.cs#L45).
- [ ] **If the subclass adds saveable state**: see "Adding new save data" below.

## Adding a new exclusive UI panel

- [ ] **Call `UI.RegisterExclusive(gameObject)` in `Awake` or `Start`** ([UI.cs](../UI/UI.cs)).
- [ ] **Open via `UI.OpenExclusive(gameObject)`**, NOT `SetActive(true)` — otherwise sibling exclusive panels won't auto-close.
- [ ] **Panel must start ACTIVE in the scene** so `Awake` fires and registration runs.

## Adding a new item sprite / icon

- [ ] **Path convention is load-bearing**: `Sprites/Items/split/<itemName>/icon`. Missing icon = silent fallback to `default/icon`.
- [ ] **After adding a sheet**: Tools → Split All → Generate Normal Maps.
- [ ] **If the sprite should be lit at runtime**: sorting layer must be one of the `litLayers` (see SPEC-rendering.md).

## Adding new save data

- [ ] **Follow the checklist comment at the top of [SaveSystem.cs](../Controller/SaveSystem.cs)** — it lists the exact Gather/Restore/Reset call sites to hit.
- [ ] **Restore order matters.** Anything that depends on animals being fully constructed goes in `PostLoadInit` (frame 2+), not the synchronous restore phase.
- [ ] **Bump `saveVersion` only for non-additive changes** (renames, removals, semantic shifts). Pure additions are forward-compatible.

---

## Cross-cutting (any new JSON content)

- [ ] **JSON parses cleanly.** Trailing commas / smart quotes silently break `Db` load.
- [ ] **No hardcoded content in C#.** If you're tempted to special-case a content name in code, you're bypassing the data-driven design.
- [ ] **New `static List<>` / `static Dictionary<>` cache in a singleton?** Reset it in the singleton's constructor, not just at declaration — otherwise scene reloads double-populate. See the reset block in [Db.cs:72-100](../Model/Db.cs#L72) for the pattern.
- [ ] **Test with an EXISTING save**, not just a fresh start. Silent orphaned references (recipe `tile` typo, renamed item) often only surface on load, never on new-world generation.
