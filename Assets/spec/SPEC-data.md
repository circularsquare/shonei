# Shonei — Data-Driven Design

All game content is defined in JSON and loaded at startup via `Db.cs` using Newtonsoft.Json. String references (e.g., `"wood"`) are resolved to object references in `[OnDeserialized]` callbacks.

Lookups: `itemByName`, `jobByName`, `structTypeByName`, `plantTypeByName`, `tileTypeByName`

## `buildingsDb.json` — StructTypes

ID ranges (keep entries ordered and thematically grouped):

| Range | Category | Examples |
|-------|----------|---------|
| 1–19 | structural / navigation | platform, stairs, ladder, road |
| 20–39 | storage | house, drawer, crate, tank, market |
| 40–79 | decoration / ambience / leisure | torch, fountain, clock, fireplace |
| 80–99 | placeable tiles | empty (dig), dirt, stone |
| 101–109 | production — basic workstations | sawmill, workshop, furnace, press, pump, paper mill |
| 110–119 | production — extraction | dirt pit, quarry |
| 120–129 | production — research | laboratory |
| 130–139 | production — clothing | weaver, tailor |

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique; defines ordering |
| `name` | string | lookup key |
| `description` | string? | shown in UI |
| `nx`, `ny` | int | footprint in tiles |
| `storageTileX` | int? | X offset of storage tile within multi-tile buildings |
| `ncosts` | `[{name, quantity}]` | build cost in liang |
| `njob` | string? | **logistics** job for this structure: who supplies its blueprint, constructs it, and deconstructs it. Defaults to `"hauler"` if omitted. NOT the operator job for workstations — that's determined per-recipe via `recipe.job`. (For plants `njob` is overloaded to mean the harvest job — see plantsDb schema.) |
| `isStorage` | bool? | has a storage inventory |
| `storageClass` | enum? | `"default"` / `"liquid"` / `"book"` — restricts which items this storage accepts (matched against `Item.itemClass`). Tanks = liquid, bookshelves = book. Defaults to `"default"`. |
| `nStacks` | int? | inventory slot count (requires `isStorage`) |
| `storageStackSize` | int? | max stack per slot in liang (requires `isStorage`) |
| `capacity` | int? | max simultaneous workers |
| `depth` | int? | render/occupancy layer: `0` building, `1` platform, `2` foreground (stairs/ladder/torch), `3` road. Defaults to 0. |
| `solidTop` | bool? | mice can stand on top |
| `isTile` | bool? | placeable tile type rather than a building |
| `category` | string | UI build menu group: `"storage"`, `"structures"`, `"tiles"`, `"production"` |
| `defaultLocked` | bool? | hidden from build menu until researched |
| `requiredTileName` | string? | tile type this building must be placed on |
| `depleteAt` | int? | production count at which this building depletes |
| `pathCostReduction` | float? | reduces A* edge cost (roads) |
| `isWorkstation` | bool? | registers a WOM Craft order when placed |
| `isDecoration` | bool? | nearby animals gain passive happiness |
| `decorationNeed` | string? | which happiness satisfaction this decoration targets (e.g. "fountain"); required when `isDecoration` is true |
| `decorRadius` | int? | Chebyshev radius for decoration effect |
| `isLeisure` | bool? | mice actively visit during leisure time (e.g. fireplace) |
| `leisureNeed` | string? | which happiness satisfaction this building targets (e.g. "fireplace"); required when `isLeisure` is true |
| `leisureGrant` | float? | multiplier on `Happiness.activityGrant` when `NoteLeisure` fires (default `1.0`). Lower values for cheap/always-on buildings (e.g. bench = `0.5`) so they don't fully substitute for premium leisure. Warmth buff on fireplace is NOT scaled. |
| `leisurePose` | string? | body pose an animal strikes while seated at this building (e.g. `"sit"`). Read by `LeisureObjective.PoseOverride` and mapped to an Animator int by `AnimationController.PoseToInt`. null/missing = default state-driven animation. See SPEC-rendering.md §Animation states & pose overrides. |
| `hasFuelInv` | bool? | building has an internal fuel reservoir |
| `fuelItemName` | string? | item consumed by reservoir (group or leaf) |
| `fuelCapacity` | float? | max fuel in liang |
| `fuelBurnRate` | float? | consumption rate in liang/day |
| `noMaintenance` | bool? | opts this StructType out of the maintenance / condition decay system. Set to `true` on nav-critical types (platform, stairs, ladder) so a neglected world doesn't cut mice off from parts of the map. Plants and cost-free structures are already auto-exempt — see SPEC-systems.md §Maintenance System. |

## `itemsDb.json` — Item types

ID ranges:

| Range | Category |
|-------|----------|
| 0 | none |
| 1–4 | currency (silver) |
| 5–9 | raw wood group + leaves (wood=5, oak=6, maple=7, pine=8) |
| 10–19 | raw stone group + leaves (stone=10, slate=11, granite=12, limestone=13) |
| 20–29 | dirt |
| 30–39 | ores (iron ore, coal) |
| 40–49 | metals (iron) |
| 60–69 | gems (gem=60, jade=61) |
| 100–119 | processed wood group + leaves (planks=100, oak planks=101, maple planks=102, pine planks=103, sawdust=110, paper=112) |
| 150–199 | food and seeds |
| 200–209 | tools and equipment |
| 210–239 | liquids and processed food (water, soymilk, tofu) |
| 250–259 | fiber group (ramie) |
| 260–269 | cloth group (ramie cloth) |
| 270–279 | clothing group (ramie shirt) |

**Item tree / group items**: Items with `children` are *group items* — they act as wildcards in recipe inputs and building costs (e.g. a building costing `"wood"` accepts any of oak/maple/pine). Group items are **never physically produced or stored**; only leaf items (those without `children`) exist in inventories. `Db.ValidateNoGroupOutputs()` logs errors at startup if a group item appears in any recipe output, plant product, or tile drop. Default wood is `pine`; default stone is `limestone`.

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | lookup key |
| `decayRate` | float | decay per tick on floors (0 = no decay); inherited by children if not specified on child |
| `foodValue` | int? | hunger restored when eaten |
| `happinessNeed` | string? | which happiness satisfaction eating this food grants (e.g. "wheat", "fruit", "soymilk"); null = none |
| `discrete` | bool? | stored/moved in whole-liang (100 fen) units only (e.g. tools, clothing); inherited by children |
| `itemClass` | enum? | `"default"` (solid), `"liquid"` (water, soymilk), `"book"` (tech/fiction books). Storage inventories match class exactly — liquids only fit in tanks, books only in bookshelves. Inherited by children. Defaults to `"default"`. |

Note: IDs 300+ are reserved for books. The `book` group (id 300) is authored in this file with `fiction_book` (id 301) as its only static child. **One tech book per research tech is generated at runtime** by `Db.GenerateBookItems()` and appended as additional children of the `book` group, with sequential IDs starting at 302. The tech-id → book-item-id map lives in `Db.bookItemIdByTechId`. All books share one sprite (`Sprites/Items/split/books/icon`).
| `liquidColorHex` | string? | `#RRGGBB` tint used when this liquid is rendered in a decorative water zone (tank/fountain); absent → shader falls back to its default water blue |
| `defaultOpen` | bool? | group items only: start expanded in the inventory tree by default (e.g. `"food"`). Groups without this flag start collapsed. Runtime collapse state overrides on click. |
| `children` | array? | leaf sub-types; see group item note above |

## `recipesDb.json` — Recipes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `job` | string | job name required to execute |
| `tile` | string | building name where recipe runs |
| `description` | string | shown in UI |
| `workload` | float | ticks to complete |
| `research` | string? | technology node that receives passive progress on each completed cycle (maintain-only) |
| `researchPoints` | float? | passive research progress granted per cycle, paired with `research` |
| `skill` | string? | skill domain for XP (e.g. `"mining"`); defaults to `job.defaultSkill` if omitted |
| `maxRoundsPerTask` | int? | cap on rounds in one CraftTask trip (0/omit = unlimited). Set to 1 for "one item per trip" recipes (e.g. book writing) where each cycle should be a deliberate, discrete action rather than a batch. |
| `ninputs` | `[{name, quantity}]` | consumed items in liang |
| `noutputs` | `[{name, quantity, chance?}]` | produced items; `chance` (0–1) = probability of output |

## `jobsDb.json` — Jobs

ID ranges:

| Range | jobType | Examples |
|-------|---------|---------|
| 0 | `"logistics"` | none (idle) |
| 1–2 | `"none"` | hauler, merchant |
| 3–9 | `"gatherer"` | logger, miner, farmer |
| 10 | `"gatherer"` | digger |
| 11–19 | `"crafter"` / `"researcher"` | woodworker, scientist, smith, cook, clothier |

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | lookup key; matched against `recipe.job` |
| `jobType` | string | category: `"logistics"`, `"none"`, `"gatherer"`, `"crafter"`, `"researcher"` |
| `defaultSkill` | string? | skill domain awarded to all recipes of this job unless overridden; null for hauler/merchant |
| `defaultLocked` | bool? | hidden from the jobs panel until a tech with a matching `{"type":"job"}` entry is unlocked |
| `skillWeights` | `{skill: float}`? | affinity weights used by auto job-swapping |

## `plantsDb.json` — PlantTypes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique (500s range) |
| `name` | string | lookup key |
| `ncosts` | `[{name, quantity}]` | planting cost in liang |
| `nproducts` | `[{name, quantity}]` | harvest yields in liang |
| `growthTime` | int | ticks to mature |
| `harvestTime` | float | ticks to harvest |
| `njob` | string | job that can harvest this plant |
| `tempMin` | float? | °C lower bound for growth — `null` = no lower bound |
| `tempMax` | float? | °C upper bound for growth |
| `moistureMin` | int? | 0–100 soil-moisture lower bound (reads `Tile.moisture`) |
| `moistureMax` | int? | 0–100 soil-moisture upper bound |
| `moistureDrawPerHour` | float? | passive draw from the soil tile below each in-game hour (default 4). Crossing into a new growth stage additionally costs `2 × this` from the same tile — see gating below |
| `maxHeight` | int? | max tile-height this plant can reach (default 1). Multi-tile plants extend upward as growth stage crosses 4-stage thresholds (stage 4 → 2 tall, stage 8 → 3 tall); max stage = `4 × maxHeight − 1`. Yield at harvest scales linearly with the plant's current height. See SPEC-systems.md "Plant Growth". |

**Comfort range gating**: `Plant.Grow()` skips its age increment when either the global ambient temperature OR the tile's `moisture` is outside the authored range. Out-of-range simply freezes growth — no withering, no stress. Null bounds mean "no limit" on that side, letting plants with unspecified ranges grow unconditionally. The helper `PlantType.IsComfortableAt(Tile, WeatherSystem)` encapsulates the range tests.

**Moisture consumption**: every in-game hour, each plant drains `moistureDrawPerHour` from the soil tile below (clamped ≥ 0; undersupplied plants simply take what's available). Additionally, whenever `Plant.Grow()` would cross into the next growth stage, it must first pay `2 × moistureDrawPerHour` from that same tile — if the soil doesn't carry that cost, the plant holds at its current stage (age also holds, keeping `stage = age*3/growthTime` coherent). This advancement cost is where moisture shortage actually gates growth; the passive draw just drains the soil over time.

## `tilesDb.json` — TileTypes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique (0=empty, 1=structure, 2=dirt, 3=limestone, 4=granite, 6=slate) |
| `name` | string | lookup key |
| `solid` | int | 0=passable, 1=solid (blocks movement) |
| `group` | string? | logical family (e.g. `"stone"` for limestone/granite/slate). `StructPlacement` treats `requiredTileName` as a match on either the tile's name or its group, so quarry's `requiredTileName: "stone"` accepts any stone variant. |
| `nproducts` | `[{name, quantity}]`? | items dropped on tile break (semantically: "clear the area"). Simple flat drops, no chance. |
| `nExtractionProducts` | `[{name, quantity, chance?}]`? | items produced each cycle by an extraction building (e.g. quarry) placed on this tile. Distinct from `nproducts` because extraction is deliberate harvesting, not mining clearance. Consumed via `Quarry.GetExtractionOutputs` → `AnimalStateManager` craft loop. |

## `researchDb.json` — Technologies

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | display name (also used to look up icon at `Sprites/Researches/{name}`) |
| `description` | string | shown in tooltip |
| `unlocks` | `[{type, target}]` | per-entry unlocks (see below) — empty array allowed |
| `prereqs` | `[int]` | required technology ids |
| `cost` | float | research points to unlock |

Each `unlocks` entry:

| Field | Type | Notes |
|-------|------|-------|
| `type` | string | `"building"`, `"recipe"`, or `"job"` |
| `target` | string | building name / recipe id (as string) / job name |

A single technology can grant multiple unlocks of mixed types. Gating uses reverse indexes built at load (`ResearchSystem.recipeToTechNode`, `jobToTechNode`, `buildingToTechNode`): anything referenced by some tech's `unlocks` entry is considered locked until that tech is unlocked. Ungated entries are always available.
