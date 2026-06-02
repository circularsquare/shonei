# Shonei — Data-Driven Design

All game content is defined in JSON and loaded at startup via `Db.cs` using Newtonsoft.Json. String references (e.g., `"wood"`) are resolved to object references in `[OnDeserialized]` callbacks.

> **Adding new content?** This doc is the schema reference. For the authoring workflow and common footguns (the `njob` operator-vs-logistics mix-up, group-vs-leaf items, etc.), read the corresponding section in [SPEC-checklists.md](SPEC-checklists.md) first.

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
| `solidTop` | bool? | mice can stand on top (also blocks rain — a roofed-over tile is by definition sheltered) |
| `blocksRain` | bool? | shelters tiles below from rain *without* being walkable on top. Used by tarps. Honoured by `World.IsExposedAbove` and `MoistureSystem.CapsSoilFromAbove` alongside `solidTop`. |
| `edgeSupported` | bool? | "edge-supported" footprint: only the leftmost and rightmost columns of the bottom row need standable support — the middle is free to hang in mid-air. Used by tarps (cloth strung between two posts). Affects both `StructPlacement.CanPlaceHere` (placement requires both ends supported) and `Blueprint.IsSuspended` (only the two ends gate suspension, not every bottom tile). |
| `isTile` | bool? | placeable tile type rather than a building |
| `placesStructureOnComplete` | string? | name of an additional Structure to place on the same tile after construction (resolved via `Db.structTypeByName`). Used by structures that bundle a follow-up structure with their placement — currently `mineshaft` → `ladder`. Placed before the post-construct standability sweep so the new structure's nav edges enter the graph in the same call. Works for both isTile and non-isTile types. |
| `category` | string | UI build menu group: `"storage"`, `"structures"`, `"tiles"`, `"production"` |
| `defaultLocked` | bool? | hidden from build menu until researched |
| `requiredTileName` | string? | tile type this building must be placed on |
| `preservesTile` | bool? | opt out of the tile-to-empty swap in `StructController.Construct()`. The footprint tiles keep their original type (grass still grows, snow accumulates, water stays blocked, support unchanged), and the structure renders in front of them as if it were a hole carved out. Yield still fires: `Blueprint.Complete` captures each footprint tile's `tile.type.products` into `pendingOutput`. Used by `burrow` (a hole into a dirt bank — multi-tile, so it yields 3× dirt). Pair with `requiredTileName` or `mustBeSolidTile`. |
| `tileRequirements` | `[{dx, dy, mustBeStandable?, mustHaveWater?, mustBeEmpty?, mustBeSolidTile?, mustBeOpenSkyAbove?, requiredTileName?}]?` | per-tile placement constraints checked at footprint offsets, evaluated by `StructPlacement.CanPlaceHere`. **Special case**: a `mustBeSolidTile: true` requirement on the placement tile itself (`dx: 0, dy: 0`) signals that this StructType is *meant* to occupy a solid tile — `StructPlacement` skips its default "reject placement on non-empty tiles" + standability rejections, AND `StructController.Construct()` mines the tile to empty after placement (mirroring the existing `requiredTileName` mining trigger). Used by `mineshaft`. |
| `depleteAt` | int? | production count at which this building depletes |
| `pathCostReduction` | float? | reduces A* edge cost (roads) |
| `isWorkstation` | bool? | registers a WOM Craft order when placed |
| `isDecoration` | bool? | nearby animals gain passive happiness |
| `decorationNeed` | string? | which happiness satisfaction this decoration targets (e.g. "fountain"); required when `isDecoration` is true |
| `decorRadius` | int? | Chebyshev radius for decoration effect |
| `isLeisure` | bool? | mice actively visit during leisure time (e.g. fireplace, brewery). May be combined with `isWorkstation` on the same building — the workstation `res` and leisure `seatRes[]` reservation systems are independent. The `brewery` is the first such dual-purpose building: a cook crafts at the work tile while a drinker stands at the keg (storage) tile. When combining, place the leisure seat on a different `nworkTiles` entry than the work tile so the two roles don't physically collide. |
| `leisureNeed` | string? | which happiness satisfaction this building targets (e.g. "fireplace", brewery → "alcohol"); required when `isLeisure` is true |
| `leisureGrant` | float? | multiplier on `Happiness.activityGrant` when `NoteLeisure` fires (default `1.0`). Lower values for cheap/always-on buildings (e.g. bench = `0.5`) so they don't fully substitute for premium leisure. Warmth buff on fireplace is NOT scaled. |
| `leisurePose` | string? | body pose an animal strikes while seated at this building (e.g. `"sit"`). Read by `LeisureObjective.PoseOverride` and mapped to an Animator int by `AnimationController.PoseToInt`. null/missing = default state-driven animation. See SPEC-rendering.md §Animation states & pose overrides. |
| `workSpotX` | float? | optional fractional x-offset (anchor-relative, in tile units) for an off-grid worker pose. When both `workSpotX` and `workSpotY` are set, `Structure` registers a Graph waypoint Node at that position and edges it to the nearest bottom-row tile-node; `Structure.workNode` then targets the waypoint instead of `workTile.node`. Used by the wheel runner to stand centred between the 2×2 footprint columns (`0.5`, `0.25`). null = use integer `workTile` (today's behaviour). Mirror formula `nx-1-x` is reused — works identically for floats. See SPEC-systems.md §Workspot waypoints. |
| `workSpotY` | float? | companion to `workSpotX`. Both must be set for the workspot waypoint to be created. |
| `workPose` | string? | body pose the worker strikes while crafting at this building (mirrors `leisurePose`). Read by `WorkObjective.PoseOverride`. The special value `"walk"` reuses the existing walk animator state instead of needing a new pose layer — used by the wheel runner so the mouse cycles its legs while producing power. Other names map via `AnimationController.PoseToInt`. null = default Working state. |
| `workView` | string? | facing-view the worker strikes while crafting here (`"back"`/`"front"`). Read by `WorkObjective.ViewOverride`, mapped by `AnimationController.ViewNameToFacing`; swaps the paper-doll to the back/front sprite set. null = default side facing. e.g. crucible = `"back"`. See SPEC-rendering.md §Facing direction. |
| `hasFuelInv` | bool? | building has an internal fuel reservoir |
| `fuelItemName` | string? | item consumed by reservoir (group or leaf) |
| `fuelCapacity` | float? | max fuel in liang |
| `fuelBurnRate` | float? | consumption rate in liang/day |
| `hasProcessor` | bool? | building has a `Processor` component — a passive timed converter (see SPEC-systems.md §Fermentation processors). The conversion itself (inputs, outputs, duration, temperature ramp, tint) is defined separately in `processorRecipesDb.json`, linked to the building by name — see the `processorRecipesDb.json` section below. The brewery is the first user. |
| `processorTileX`, `processorTileY` | int? | tile offset of the processor's inventory tile within the footprint. This is footprint geometry, so it stays on the building — unlike the recipe data, which lives in `processorRecipesDb.json`. |
| `noMaintenance` | bool? | opts this StructType out of the maintenance / condition decay system. Set to `true` on nav-critical types (platform, stairs, ladder) so a neglected world doesn't cut mice off from parts of the map. Plants and cost-free structures are already auto-exempt — see SPEC-systems.md §Maintenance System. |
| `placementMethod` | string? | When `"twoClick"`, the StructType is placed by clicking TWO tiles (the two endpoint posts of a rope bridge). First click stashes the post; second click commits a single blueprint carrying both endpoints. Defaults to single-click placement. See SPEC-systems.md §Rope bridges. |
| `minDx`, `maxDx` | int? | Horizontal-delta bounds for two-click placement. Bridge requires `minDx ≤ |xA - xB| ≤ maxDx`. Defaults: 3 / 20. |
| `maxDy` | int? | Vertical-delta cap for two-click placement (`|yA - yB| ≤ maxDy`). Default 5. |
| `sagFraction` | float? | Catenary sag amount as a fraction of `|Δx|`. Higher = more droop. Default 0.15. Drawn from horizontal delta only (not euclidean length) so steep bridges don't sag below their lower endpoint. |
| `shapes` | `[{nx, ny, standableOffsets?}]?` | optional list of variable-shape footprint variants. When present, the player cycles between them with Q/E during build placement; the chosen shape's `nx`/`ny` override the StructType's base footprint on the placed structure & blueprint. `shapes[0]` is the "authored" baseline — `ncosts` are sized for it, and other shapes scale linearly with tile count. v1 supports vertical extension only (`nx=1, ny>=1`). Optional `standableOffsets: [{dx, dy}, …]` per shape declares walkable tiles inside the footprint that the default solidTop / multi-tile-body rule wouldn't expose — read by `Structure.HasInternalFloorAt` (mirroring applied at lookup; author offsets un-mirrored). Lets non-subclassed multi-tile buildings publish partial-top patterns from JSON. See SPEC-systems.md §Variable-shape structures. |
| `isHousing` | bool? | canonical "this building is a place mice live." Replaces the legacy `structType.name == "house"` check that `FindHome` / `AtHome` / `HasHouse` / `TotalHousingCapacity` / InfoPanel occupants all read. Adding a new housing tier is JSON-only — set this flag and the system picks it up. See SPEC-systems.md §Housing assignment. |
| `interiorTiles` | `[{dx, dy}]?` | footprint tiles that should have *interior* walking space. Each entry causes `Structure` to register an off-grid `Node` inside that tile (mirror-aware via `nx-1-dx`) and mark the tile with `Tile.interiorBuilding` back-pointing at the building, so `Animal.insideBuilding` derives correctly while a mouse stands there. Interior nodes auto-edge horizontally to neighbours (Manhattan `\|dx\|=1, dy=0`); vertical access requires `ladders[]`. Used by housing (shack, house, burrow) and any future "you can enter this building" type. See SPEC-systems.md §Door + interior waypoints. |
| `doors` | `[{dx, dy, side}]?` | door declarations. `(dx, dy)` is the footprint tile the door sits in (must also appear in `interiorTiles`); `side` is `"left"` / `"right"` / `"top"` / `"bottom"` and selects the *approach tile* — the existing graph node just outside the door on that side. Each door becomes a single bidirectional graph edge between the interior node and the approach tile's node. Mirror handling: `dx` flips, `left ↔ right` swap; top/bottom unaffected. Multiple doors are allowed (e.g. burrow with side + roof). |
| `ladders` | `[{dx, dy}]?` | explicit vertical edges between interior nodes. Each entry connects the interior node at `(dx, dy)` to `(dx, dy+1)`. Required because horizontal-adjacent edging is automatic but vertical isn't — authors choose where mice climb (e.g. "ladder on the left of a 2×2 house") instead of mice climbing through floors. Multiple ladders allowed for taller stacks or multiple climb points. Mirror-aware (`nx-1-dx`). |

## `itemsDb.json` — Item types

ID ranges:

| Range | Category |
|-------|----------|
| 0 | none |
| 1–4 | currency (silver) |
| 5–9 | raw wood group + leaves (wood=5, oak=6, maple=7, pine=8) |
| 10–19 | raw stone group + leaves (stone=10, slate=11, granite=12, limestone=13) |
| 20–29 | earth items (dirt=20, sand=21, clay=22) |
| 30–39 | ores (ore group=30, iron ore=31, coal=32, malachite=33, cassiterite=34) |
| 40–49 | metals (iron bar=40, copper bar=41, tin bar=42, bronze bar=43) |
| 60–69 | gems (gem=60, jade=61) |
| 100–119 | processed wood group + leaves (planks=100, oak planks=101, maple planks=102, pine planks=103, sawdust=110, paper=112) |
| 150–199 | food and seeds |
| 200–209 | tools group + leaves (tools=200, stone tools=201, copper tools=202, bronze tools=203) |
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
| `decayRate` | float | passive decay per tick (multiplied by per-`InvType` factor: Floor 5×, Storage 1×, Equip 1×, Animal/Market/Blueprint/Reservoir/Furnishing 0); 0 = no passive decay; inherited by children if not specified on child. Units are "per in-game year" — see `ItemStack.Decay`. |
| `equipDecayRate` | float? | extra wear ticked only while the item sits in an animal's Equip slot AND the animal is in the Working state (HandleWorking). Same per-year units as `decayRate`. Deterministic — shares `ItemStack.decayCounter` with passive decay so both contributions accumulate to the same wear pool. Set on tools (and could be set on clothing) to make wear scale with how much the animal actually works. 0/absent = no use-based wear. |
| `workEfficiency` | float? | multiplier applied to `ModifierSystem.GetWorkMultiplier` when this item is equipped in the tool slot. 1.0 (default) = "no tool" — empty slot and a workEfficiency-1 item are indistinguishable. >1 = active bonus. Stone tools 1.10, copper 1.20, bronze 1.30; reserve ~1.5 for far-future endgame tools. Meaningless on non-tool items where it stays 1.0. |
| `foodValue` | int? | hunger restored when eaten |
| `happinessNeed` | string? | which happiness satisfaction eating this food grants (e.g. "wheat", "fruit", "soymilk"); null = none |
| `discrete` | bool? | stored/moved in whole-liang (100 fen) units only (e.g. tools, clothing); inherited by children |
| `itemClass` | enum? | `"default"` (solid), `"liquid"` (water, soymilk), `"book"` (tech/fiction books). Storage inventories match class exactly — liquids only fit in tanks, books only in bookshelves. Inherited by children. Defaults to `"default"`. |
| `defaultTarget` | int? | initial global production target in liang. Used by `InventoryController.Start` to seed `targets[itemId]`, and by recipe / harvest scoring (`Recipe.Score`, `Recipe.AllItemsSatisfied`) as the threshold. Defaults to `100`; lower for byproducts (acorn, sawdust, pinecone are `10`) so multi-product harvest gating actually triggers. Books override to 1 liang via `itemClass==Book` regardless of this field. SaveSystem only persists deltas vs this default. |

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
| `hidden` | bool? | omit from the Recipes panel (still craftable). Used for non-conventional pseudo-recipes like `dig` / `mine stone` whose "workstation" (digging pit, quarry) shouldn't appear as a recipe group. |
| `ninputs` | `[{name, quantity}]` | consumed items in liang |
| `noutputs` | `[{name, quantity, chance?}]` | produced items; `chance` (0–1) = probability of output |

**Recipes panel display notes:**
- `description` should stay short — `Db.WarnLongRecipeNames()` logs a warning at load for any longer than the reference string `"smelt malachite into copper (wood-"` (34 chars), since long names truncate in the card header.
- Book-writing recipes (any recipe whose single output is `ItemClass.Book` — the runtime per-tech books + authored `fiction_book`) collapse in the panel into **one** generic "write a book" card per workstation; its On/Off toggles all book recipes together. See `RecipePanel.IsBookRecipe` / `BuildBookProxy`.

## `processorRecipesDb.json` — Processor recipes

A **processor recipe** is the passive timed conversion run by a building's `Processor` component (see SPEC-systems.md §Fermentation processors). It is a distinct concept from a craft `Recipe`: no active labor (`processDays` is wall-clock, not work-ticks), an optional temperature ramp, no job/skill/scoring model. Each recipe is linked to a building by name — mirroring how `Recipe.tile` links a craft recipe to its workstation. One building runs one recipe today; the data model (`Db.processorRecipesByBuilding`, a list per building) already allows several, so multiple-processes-per-building is an additive future extension.

Loaded into `Db.processorRecipesByBuilding` (keyed by building name); `Db.GetProcessorRecipe(name)` resolves a building's recipe. `Db.ValidateProcessorRecipes()` cross-checks that every `hasProcessor` building has a recipe and every recipe targets a real building.

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | informational — recipes are keyed by building name, not array-indexed. Duplicate ids are logged. Use a high range (≥1000) to avoid confusion with craft `Recipe` ids, which share no namespace but can collide numerically. |
| `building` | string | name of the building whose `Processor` runs this recipe |
| `description` | string | shown in UI / InfoPanel |
| `ninputs` | `[{name, quantity}]` | the load recipe, authored in liang, resolved to fen |
| `noutputs` | `[{name, quantity}]` | what one batch yields, authored in liang |
| `processDays` | float | base conversion duration in in-game days at full (temperature rate 1.0) speed |
| `processTempMin`, `processTempIdeal` | float? | optional temperature ramp: rate is 0 at/below `processTempMin`, 1.0 at/above `processTempIdeal`, linear between. Omit both → constant full rate. |
| `autoTap` | bool? | schema stub — reserved for processors that yield output without a manual tap. Not yet implemented. |
| `processColorHex` | string? | `#RRGGBB` tint for the building's `_w` liquid zone while the processor is Working (e.g. cloudy white rice mash mid-fermentation). Absent → the zone keeps its loading colour. See SPEC-rendering.md §Decorative liquid zones. |

**Recipes panel:** processes appear as cards grouped under their building alongside that building's craft recipes. A process card shows the brew time (e.g. `2d`) in place of the worker-count line and an On/Off toggle that pauses the process — keyed by building name in `RecipePanel.disabledProcesses`, enforced by gating the `FillProcessor` work order's `isActive` (new fills stop; an in-progress batch still finishes + taps). Persisted as `WorldSaveData.disabledProcesses`.

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
| `moistureDrawPerHour` | float? | passive draw from the soil tile below each in-game hour (default 2). Crossing into a new growth stage additionally costs `2 × this` from the same tile — see gating below |
| `maxHeight` | int? | max tile-height this plant can reach (default 1). Multi-tile plants extend upward as growth stage crosses 4-stage thresholds (stage 4 → 2 tall, stage 8 → 3 tall); max stage = `4 × maxHeight − 1`. Yield at harvest scales linearly with the plant's current height. |
| `genWeight` | float? | relative weight for `WorldGen.ScatterPlants` to pick this plant type for natural clusters. Unnormalized — sampled proportionally against every other plant type with `genWeight > 0`. Default 0 = never spawns naturally (crops planted only by the player, legacy types). |

For the gameplay mechanics behind these fields (comfort gating, per-hour moisture draw, stage-advance cost, height extension), see SPEC-systems.md §Plant Growth.

## `tilesDb.json` — TileTypes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique (0=empty, 1=structure, 2=dirt, 3=sand, 4=clay, 20=limestone, 21=granite, 22=slate). Solid-tile ids drive the soft-edge sort ranking — lower id draws on top at different-type boundaries (see SPEC-rendering "Tile body sort order"). |
| `name` | string | lookup key |
| `solid` | int | 0=passable, 1=solid (blocks movement) |
| `group` | string? | logical family (e.g. `"stone"` for limestone/granite/slate, `"earth"` for dirt/sand/clay). `StructPlacement` treats `requiredTileName` as a match on either the tile's name or its group, so quarry's `requiredTileName: "stone"` accepts any stone variant and digging pit's `"earth"` accepts dirt/sand/clay. **Watch out**: name-only matches (e.g. burrow's `requiredTileName: "dirt"`) skip the group, so a burrow only digs into the dirt tile even though dirt is in the `"earth"` group. |
| `overlay` | string? | name of an overlay sprite sheet that tiles of this type can carry per-side decoration from. `dirt → "grass"` today; future moss-on-stone would set this on stone variants. Loads from `Resources/Sprites/Tiles/Sheets/<overlay>.png` (32×32 atlas, transparent Main interior). See SPEC-rendering "Tile overlays" for the rendering trick. |
| `nproducts` | `[{name, quantity}]`? | items dropped on tile break (semantically: "clear the area"). Simple flat drops, no chance. |
| `nExtractionProducts` | `[{name, quantity, chance?}]`? | items produced each cycle by an extraction building (`Quarry`) placed on this tile. Distinct from `nproducts` because extraction is deliberate harvesting, not mining clearance. Consumed via `Quarry.GetExtractionOutputs` → `AnimalStateManager` craft loop. (`DiggingPit` uses a different mechanism — it reads `nproducts[0]` and emits one liang per craft, scaled by substrate; see `DiggingPit.GetExtractionOutputs`.) |

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
