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
| 101–109 | production — basic workstations | sawmill, workshop, furnace, press, pump |
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
| `njob` | string? | job assigned on placement (e.g. `"hauler"`) |
| `isStorage` | bool? | has a storage inventory |
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
| `hasFuelInv` | bool? | building has an internal fuel reservoir |
| `fuelItemName` | string? | item consumed by reservoir (group or leaf) |
| `fuelCapacity` | float? | max fuel in liang |
| `fuelBurnRate` | float? | consumption rate in liang/day |

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
| 100–119 | processed wood group + leaves (planks=100, oak planks=101, maple planks=102, pine planks=103, sawdust=110) |
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
| `isLiquid` | bool? | liquid item (water, soymilk); only fits in liquid-storage containers; inherited by children |
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
| `research` | string? | research node name required to unlock |
| `skillPoints` | float? | skill gained per completion |
| `skill` | string? | skill domain for XP (e.g. `"mining"`); defaults to `job.defaultSkill` if omitted |
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
| 11–19 | `"crafter"` / `"researcher"` | sawyer, scientist, smith, cook, clothier |

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | lookup key; matched against `recipe.job` |
| `jobType` | string | category: `"logistics"`, `"none"`, `"gatherer"`, `"crafter"`, `"researcher"` |
| `defaultSkill` | string? | skill domain awarded to all recipes of this job unless overridden; null for hauler/merchant |

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

## `tilesDb.json` — TileTypes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique (0=empty, 1=structure, 2=dirt, 3=stone) |
| `name` | string | lookup key |
| `solid` | int | 0=passable, 1=solid (blocks movement) |
| `nproducts` | `[{name, quantity}]`? | items dropped when mined |

## `researchDb.json` — Research nodes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | display name |
| `description` | string | shown in tooltip |
| `type` | string | `"building"`, `"recipe"`, or `"misc"` |
| `unlocks` | string | building name, recipe id, or effect key (e.g. `"research_efficiency"`) |
| `prereqs` | `[int]` | required research node ids |
| `cost` | int | research points to unlock |
