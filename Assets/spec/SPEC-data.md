# Shonei — Data-Driven Design

All game content is defined in JSON and loaded at startup via `Db.cs` using Newtonsoft.Json. String references (e.g., `"wood"`) are resolved to object references in `[OnDeserialized]` callbacks.

Lookups: `itemByName`, `jobByName`, `structTypeByName`, `plantTypeByName`, `tileTypeByName`

## `buildingsDb.json` — StructTypes

ID ranges (keep entries ordered and thematically grouped):

| Range | Category |
|-------|----------|
| 0 | dig (empty tile designator) |
| 1–9 | storage buildings (house, drawer, crate, market) |
| 10–19 | structural pieces (platform, stairs, ladder) |
| 20–29 | decorations / terrain features (torch, road) |
| 50–59 | placeable tile fills (dirt, stone) |
| 100–119 | material processing (sawmill, workshop, furnace) |
| 120–129 | extraction (dirt pit, quarry) |
| 130–139 | research (laboratory) |

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

## `itemsDb.json` — Item types

ID ranges:

| Range | Category |
|-------|----------|
| 0 | none |
| 1–9 | currency (silver) |
| 5–9 | raw wood |
| 10–19 | raw stone |
| 20–29 | dirt |
| 30–39 | ores (iron ore, coal) |
| 40–49 | metals (iron) |
| 100–119 | processed wood (planks, sawdust) |
| 150–199 | food and seeds |
| 200+ | tools and equipment |

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | lookup key |
| `decayRate` | float | decay per tick on floors (0 = no decay) |
| `foodValue` | int? | hunger restored when eaten |
| `discrete` | bool? | stored/moved in whole-liang (100 fen) units only (e.g. tools) |
| `children` | array? | sub-types satisfied by this type in recipes (e.g. `"wood"` matches `"oak"`, `"maple"`) |

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
| 11–19 | `"crafter"` / `"researcher"` | sawyer, scientist, smith |

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
