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
| 101–109 | production — basic workstations | sawmill, workshop, crucible, foundry, press, pump, paper mill |
| 110–119 | production — extraction | dirt pit, quarry |
| 120–129 | production — research | laboratory |
| 130–139 | production — clothing | weaver, tailor |

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique; defines ordering |
| `name` | string | **internal** lookup key (referenced by recipes, saves, edge-mask bake). Never shown to the player. |
| `displayName` | string? | optional player-facing name; falls back to `name` via `StructType.DisplayName`. Used by the build menu, InfoPanel tabs, and RecipeGroupDisplay header. Lets the internal key differ from the label (e.g. `rope bridge post` → "rope bridge", plants `pinetree` → "pine tree"). |
| `description` | string? | build-menu hover tooltip body (a `Tooltippable` attached in `BuildPanel.AddBuildDisplay`). Keep concise per the player-facing-text rules. |
| `nx`, `ny` | int | footprint in tiles |
| `storageTileX` | int? | X offset of storage tile within multi-tile buildings |
| `ncosts` | `[{name, quantity}]` | build cost in liang |
| `njob` | string? | **logistics** job for this structure: who supplies its blueprint, constructs it, and deconstructs it. Defaults to `"hauler"` if omitted. NOT the operator job for workstations — that's determined per-recipe via `recipe.job`. (For plants `njob` is overloaded to mean the harvest job — see plantsDb schema.) |
| `isStorage` | bool? | has a storage inventory |
| `storageClass` | enum? | `"default"` / `"liquid"` / `"book"` — restricts which items this storage accepts (matched against `Item.itemClass`). Tanks = liquid, bookshelves = book. Defaults to `"default"`. |
| `nStacks` | int? | inventory slot count (requires `isStorage`) |
| `storageStackSize` | int? | max stack per slot in liang (requires `isStorage`) |
| `capacity` | int? | max simultaneous workers |
| `depth` | int? | render/occupancy layer: `0` building, `1` platform, `2` foreground (stairs/ladder/torch), `3` road, `4` power shaft, `5` enclosure (greenhouse). One structure per depth per tile, so a dedicated depth lets a structure coexist with the other layers. Defaults to 0. |
| `solidTop` | bool? | mice can stand on top (also blocks rain — a roofed-over tile is by definition sheltered) |
| `blocksRain` | bool? | shelters tiles below from rain *without* being walkable on top. Used by tarps. Honoured by `World.IsExposedAbove` and `MoistureSystem.CapsSoilFromAbove` alongside `solidTop`. |
| `sunPermeable` | bool? | lets sunlight through despite `solidTop` — excludes the structure from `World.BlocksSun` so it doesn't shade plants. Used by platforms (slatted). Greenhouses are sun-exempt automatically via `isGreenhouse` (glass). |
| `edgeSupported` | bool? | Support model override. **Default (unset)**: *every* column of the footprint's bottom row must rest on standable support. **`true`**: only the leftmost and rightmost columns need support — the middle may hang in mid-air (tarps strung between two posts). Either way the rule is enforced identically in `StructPlacement.GetPlacementFailReason` (placement) and `Blueprint.IsSuspended` (suspension). Rope-bridge posts are 1×1 `twoClick` placements validated per-post, so they get "both ends supported" for free without this flag. A type with an explicit `mustBeStandable` tileRequirement (see `tileRequirements`) bypasses this generic check entirely. |
| `isTile` | bool? | placeable tile type rather than a building |
| `placesStructureOnComplete` | string? | name of an additional Structure to place on the same tile after construction (resolved via `Db.structTypeByName`). Used by structures that bundle a follow-up structure with their placement — currently `mineshaft` → `ladder`. Placed before the post-construct standability sweep so the new structure's nav edges enter the graph in the same call. Works for both isTile and non-isTile types. |
| `category` | string | UI build menu group: `"storage"`, `"structures"`, `"tiles"`, `"production"` |
| `defaultLocked` | bool? | hidden from build menu until researched |
| `sideMounted` | bool? | hangs on a vertical wall (terrain or a building's body face) in an air tile instead of resting on the ground — uses the side-attach placement branch in `StructPlacement`, skips standability. Player picks the wall side via mirror (F). Used by `ladder_side`, `bracket`, `torch_side`. Mining the wall drops the mount (`Blueprint.DestroyDependentSideMounts`). See SPEC-systems.md §Side-mounted structures. |
| `sideVariant` | string? | name of the side-mounted variant this build tool auto-swaps to when the cursor hovers near a tile edge (`ladder`→`ladder_side`, `torch`→`torch_side`). The tool keeps showing/charging the base type; only the placed structure switches (`BuildPanel.ResolveSideVariant`). See SPEC-systems.md §Side-mounted structures. |
| `requiredTileName` | string? | tile type this building must be placed on |
| `preservesTile` | bool? | opt out of the tile-to-empty swap in `StructController.Construct()`. The footprint tiles keep their original type (grass still grows, snow accumulates, water stays blocked, support unchanged), and the structure renders in front of them as if it were a hole carved out. Yield still fires: `Blueprint.Complete` captures each footprint tile's `tile.type.products` into `pendingOutput`. Used by `burrow` (a hole into a dirt bank — multi-tile, so it yields 3× dirt). Pair with `requiredTileName` or `mustBeSolidTile`. |
| `tileRequirements` | `[{dx, dy, mustBeStandable?, mustHaveWater?, mustBeEmpty?, mustBeSolidTile?, mustBeOpenSkyAbove?, requiredTileName?}]?` | per-tile placement constraints checked at footprint offsets, evaluated by `StructPlacement.CanPlaceHere`. **`mustBeStandable` overrides support**: declaring it on *any* tile means the type names exactly which columns bear weight, so the generic bottom-row support check (see `edgeSupported`) is skipped and these reqs alone decide it — used by the `pump` (only the building tile dx0 needs support; the spout dx1 overhangs water). **Special case**: a `mustBeSolidTile: true` requirement on the placement tile itself (`dx: 0, dy: 0`) signals that this StructType is *meant* to occupy a solid tile — `StructPlacement` skips its default "reject placement on non-empty tiles" + standability rejections, AND `StructController.Construct()` mines the tile to empty after placement (mirroring the existing `requiredTileName` mining trigger). Used by `mineshaft`. |
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
| `workView` | string? | facing-view the worker strikes while working here (`"back"`/`"front"`). Read by `WorkObjective.ViewOverride` (workstations, crafting) and `ResearchObjective.ViewOverride` (labs), mapped by `AnimationController.ViewNameToFacing`; swaps the paper-doll to the back/front sprite set. null = default side facing. e.g. crucible/sawmill/laboratory = `"back"`. (Construction back-facing is separate — positional, in `ConstructObjective`, not this field.) See SPEC-rendering.md §Facing direction. |
| `hasFuelInv` | bool? | building has an internal fuel reservoir |
| `fuelItemName` | string? | OPTIONAL restriction on what the reservoir burns (group or leaf). Absent = accept any fuel (`fuelValue>0`), picked at refuel by `PickFuel`. See SPEC-systems §Fuel Inventory. |
| `fuelCapacity` | float? | max fuel in liang |
| `fuelBurnRate` | float? | consumption rate in **energy/day**, divided by the stocked fuel's `fuelValue` at burn (wood=1 → unchanged; coal=3 lasts 3× longer) |
| `isLightSource` | bool? | building emits a point light (requires `hasFuelInv`; the reservoir powers it). Burns + emits only while dark (`SunController.torchFactor > 0`) and lit. See SPEC-rendering.md §Fire sprites. |
| `lightIntensity` | float? | base point-light intensity (`LightSource.baseIntensity`, default 0.80). |
| `lightOuterRadius` | float? | light reach in world units (default 10). Smaller = tighter + cheaper. |
| `lightInnerRadius` | float? | flat-bright core radius; falloff ramps inner→outer (default 4). |
| `lightCenterFlatten` | float? | 0 = raw NdotL hot-spot, 1 = flat-center fill that still respects normals. |
| `lightFlicker` | float? | subtle intensity flicker amplitude as a fraction (0 = steady, `0.07` = ±7%). Per-instance Perlin lane (phase derived from position) so neighbours don't pulse in sync. Applied in `LightSource.Update`. |
| `fireSprite` | string? | flame-overlay sheet name (default `{name}_f`). Authored at host tile size with the flame at the wick; rendered as a full-tile overlay child at `localPosition (fireOffsetX, fireOffsetY)`, `sortingOrder`+1 (draws over the body), `flipX`=mirrored — so it shares the body's pixel-snap rounding. See SPEC-rendering.md §Fire sprites. |
| `fireOffsetX`, `fireOffsetY` | float? | flame child local offset in tile units (default 0; position is normally baked into the overlay art so 0 is typical). X flips with mirror. |
| `fireFps` | float? | flame animation step rate (default 7). Fire flames step via a ±1 random walk (`FrameAnimator.randomWalk`), not a fixed cycle. |
| `hasProcessor` | bool? | building has a `Processor` component — a batch converter (see SPEC-systems.md §Processors). Its conversions are ordinary `Recipe`s with `tile == this building` and a `duration` (see Processor recipes below). The brewery (untended) + cauldron (tended) are the users. |
| `processorTended` | bool? | `true` = the Working phase is worker-tended (cauldron — a worker labours for the batch's `duration`, then it auto-taps); `false`/omitted = passive ferment (brewery — advances on world ticks, temperature-scaled, then a worker taps). |
| `processorCapacityLiang` | int? | the pot's liquid capacity in liang — the denominator for the rendered fill, so one batch in a bigger pot reads partially full (the cauldron's `10` makes a 5-liang batch render half). `0`/omit → sized to one batch (reads full). Also the `output` buffer size AND the ceiling on multi-round batches (a pot bigger than one round brews several rounds per batch — see SPEC-systems §Processors). A tended processor doesn't need `isWorkstation` (work-spot comes from `nworkTiles`); set it only if the building also runs craft recipes (the brewery does, for yeast). |
| `processorTileX`, `processorTileY` | int? | tile offset of the processor's inventory tile within the footprint (footprint geometry). |
| `foundryCapacityLiang` | int? | the dedicated `Foundry` melt-pool subclass only (NOT a processor): total ore + molten capacity in liang (×100 → fen), bounding intake + chunks + molten pool. Requires `hasFuelInv`. See SPEC-systems.md §Foundry. |
| `noMaintenance` | bool? | opts this StructType out of the maintenance / condition decay system. Set to `true` on nav-critical types (platform, stairs, ladder) so a neglected world doesn't cut mice off from parts of the map. Plants and cost-free structures are already auto-exempt — see SPEC-systems.md §Maintenance System. |
| `placementMethod` | string? | When `"twoClick"`, the StructType is placed by clicking TWO tiles (the two endpoint posts of a rope bridge). First click stashes the post; second click commits a single blueprint carrying both endpoints. Defaults to single-click placement. See SPEC-systems.md §Rope bridges. |
| `minDx`, `maxDx` | int? | Horizontal-delta bounds for two-click placement. Bridge requires `minDx ≤ |xA - xB| ≤ maxDx`. Defaults: 3 / 20. |
| `maxDy` | int? | Vertical-delta cap for two-click placement (`|yA - yB| ≤ maxDy`). Default 5. |
| `sagFraction` | float? | Catenary sag amount as a fraction of `|Δx|`. Higher = more droop. Default 0.15. Drawn from horizontal delta only (not euclidean length) so steep bridges don't sag below their lower endpoint. |
| `shapes` | `[{nx, ny, standableOffsets?}]?` | optional list of variable-shape footprint variants. When present, the player cycles between them with Q/E during build placement; the chosen shape's `nx`/`ny` override the StructType's base footprint on the placed structure & blueprint. `shapes[0]` is the "authored" baseline — `ncosts` are sized for it, and other shapes scale linearly with tile count. v1 supports vertical extension only (`nx=1, ny>=1`). Optional `standableOffsets: [{dx, dy}, …]` per shape declares walkable tiles inside the footprint that the default solidTop / multi-tile-body rule wouldn't expose — read by `Structure.HasInternalFloorAt` (mirroring applied at lookup; author offsets un-mirrored). Lets non-subclassed multi-tile buildings publish partial-top patterns from JSON. See SPEC-systems.md §Variable-shape structures. |
| `isHousing` | bool? | canonical "this building is a place mice live." Replaces the legacy `structType.name == "house"` check that `FindHome` / `AtHome` / `HasHouse` / `TotalHousingCapacity` / InfoPanel occupants all read. Adding a new housing tier is JSON-only — set this flag and the system picks it up. See SPEC-systems.md §Housing assignment. |
| `isWorkFlag` | bool? | building is a work flag — mice can be assigned to it (info panel) so it becomes their work anchor (work + idle around it, not home). Drives the assignment widget + `Animal.AssignToFlag`. JSON-only marker (`work flag`, id 200). See SPEC-ai.md §Work anchors. |
| `isGreenhouse` | bool? | this structure is a climate frame plants grow inside. Sits at the dedicated enclosure `depth` (5) so it shares a tile with the plant it covers (slot 0) and any ladder/rope (slot 2) without contending for a slot; footprint tiles back-point via `Tile.greenhouse`. A plant rooted on a covered tile reads the four `greenhouse*` tunables below. See SPEC-systems.md §Plant Growth (greenhouse). |
| `greenhouseTargetTempC` | float? | interior temp (°C) the frame pulls ambient toward each growth tick (default `26.7` ≈ 80°F). NOTE: authored in **Celsius** like all temps. |
| `greenhouseTempPull` | float? | fraction 0–1 of `(target − ambient)` applied before the plant's temp gate (default `0.5` = halfway → imperfect; a stronger greenhouse sets it toward `1.0`). |
| `greenhouseGrowthMult` | float? | growth-rate multiplier for plants inside (default `1.1` = +10%). |
| `greenhouseMoistureMult` | float? | scales BOTH passive transpiration draw (`MoistureSystem`) AND the per-stage moisture cost (`Plant.Grow`) for plants inside (default `0.5` = half). |
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
| 30–39 | `ore` group=30 (iron ore=31, coal=32); `ores` group=33 (malachite=34, cassiterite=35) |
| 40–49 | `metal` group=40 (iron bar=41, copper bar=42, tin bar=43, bronze bar=44) |
| 60–69 | gems (gem=60, jade=61) |
| 100–119 | processed wood group + leaves (planks=100, oak planks=101, maple planks=102, pine planks=103, sawdust=110, paper=112) |
| 150–199 | food and seeds |
| 200–209 | tools group + leaves (tools=200, stone tools=201, copper tools=202, bronze tools=203) |
| 210–239 | liquids and processed food (water, soymilk, tofu) |
| 250–259 | fiber group (ramie) |
| 260–269 | cloth group (ramie cloth) |
| 270–279 | clothing group (ramie shirt) |
| 400–409 | herbs group + leaves (herbs=400, goji=401, mugwort=402, chrysanthemum=403, moonlily=404) — foraged via `WildHerbSystem`. Note: items live below the `Item[500]` cap, so this range must stay < 500. |

**Item tree / group items**: Items with `children` are *group items* — they act as wildcards in recipe inputs and building costs (e.g. a building costing `"wood"` accepts any of oak/maple/pine). Group items are **never physically produced or stored**; only leaf items (those without `children`) exist in inventories. `Db.ValidateNoGroupOutputs()` logs errors at startup if a group item appears in any recipe output, plant product, or tile drop. Default wood is `pine`; default stone is `limestone`.

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `name` | string | lookup key |
| `description` | string? | optional player-facing tooltip body shown on icon hover (`ItemIcon`); absent/empty = name-only tooltip, as before. Keep concise. |
| `decayRate` | float | passive decay per tick (multiplied by per-`InvType` factor: Floor 5×, Storage 1×, Equip 1×, Animal/Market/Blueprint/Reservoir/Furnishing 0); 0 = no passive decay; inherited by children if not specified on child. Units are "per in-game year" — see `ItemStack.Decay`. |
| `equipDecayRate` | float? | extra wear ticked only while the item sits in an animal's Equip slot AND the animal is in the Working state (HandleWorking). Same per-year units as `decayRate`. Deterministic — shares `ItemStack.decayCounter` with passive decay so both contributions accumulate to the same wear pool. Set on tools (and could be set on clothing) to make wear scale with how much the animal actually works. 0/absent = no use-based wear. |
| `workEfficiency` | float? | multiplier applied to `ModifierSystem.GetWorkMultiplier` when this item is equipped in the tool slot. 1.0 (default) = "no tool" — empty slot and a workEfficiency-1 item are indistinguishable. >1 = active bonus. Stone tools 1.10, copper 1.20, bronze 1.30; reserve ~1.5 for far-future endgame tools. Meaningless on non-tool items where it stays 1.0. |
| `foodValue` | int? | hunger restored when eaten |
| `fuelValue` | float? | burnable energy per liang; >0 marks the item as fuel (coal 3, `wood` group 1 → cascades to leaves). Recipes burn an abstract `fuelCost` energy satisfied by *any* fuel item, so one recipe accepts coal OR wood at potency-scaled quantity (1 coal = 3 wood). Picked + consumed via `GlobalInventory.PickFuel`/`CanCraft`. 0/absent = not fuel. Inherited by children. |
| `happinessNeed` | string? | which happiness satisfaction eating this food grants (e.g. "wheat", "fruit", "soymilk"); null = none |
| `discrete` | bool? | stored/moved in whole-liang (100 fen) units only (e.g. tools, clothing); inherited by children |
| `itemClass` | enum? | `"default"` (solid), `"liquid"` (water, soymilk), `"book"` (tech/fiction books). Storage inventories match class exactly — liquids only fit in tanks, books only in bookshelves. Inherited by children. Defaults to `"default"`. |
| `defaultTarget` | int? | initial global production target in liang. Used by `InventoryController.Start` to seed `targets[itemId]`, and by recipe / harvest scoring (`Recipe.Score`, `Recipe.AllItemsSatisfied`) as the threshold. Defaults to `100`; lower for byproducts (acorn, sawdust, pinecone are `10`) so multi-product harvest gating actually triggers. Books override to 1 liang via `itemClass==Book` regardless of this field. SaveSystem only persists deltas vs this default. |
| `liquidColorHex` | string? | `#RRGGBB` tint used when this liquid is rendered in a decorative water zone (tank/fountain); absent → shader falls back to its default water blue |
| `buffType` | string? | marks this item a **tonic**: `"workSpeed"` / `"coldTolerance"` / `"heatTolerance"` / `"sleepRecovery"`. Drinking it (`DrinkTonicTask`) applies a timed `BuffSet` effect — see SPEC-systems §Timed buffs. Parsed to the `BuffType` enum (`Item.buffEffect`) in `OnDeserialized`; `Db.tonicItems` lists all tonics. Absent = not a tonic. |
| `buffMagnitude` | float? | tonic effect strength: work/sleep = fractional bonus (`0.1` = +10%); temperature = °C of tolerance added to the comfort bound. |
| `buffDuration` | float? | tonic effect duration in in-game **days** (×`ticksInDay` → game-seconds at apply time). |
| `defaultOpen` | bool? | group items only: start expanded in the inventory tree by default (e.g. `"food"`). Groups without this flag start collapsed. Runtime collapse state overrides on click. |
| `excludeFromGroupInput` | bool? | leaf only: the AI never auto-substitutes this leaf for a **group** input/cost. Set on `gypsum` so it stays sorted under `stone` (display/refunds unaffected) and is usable where a recipe names `gypsum` directly (tofu), but is never picked to satisfy a `"stone"` requirement (buildings, stone tools). Consulted by `Task.ResolveConsumeLeaf` and `Recipe.GeoMeanInputs` (scoring) so selection and scoring agree. 0/absent = normal. |
| `children` | array? | leaf sub-types; see group item note above |

Note: IDs 300+ are reserved for books. The `book` group (id 300) is authored in this file with `fiction_book` (id 301) as its only static child. **One tech book per research tech is generated at runtime** by `Db.GenerateBookItems()` and appended as additional children of the `book` group, with sequential IDs starting at 302. The tech-id → book-item-id map lives in `Db.bookItemIdByTechId`. All books share one sprite (`Sprites/Items/split/books/icon`).

## `recipesDb.json` — Recipes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique |
| `job` | string | job name required to execute |
| `tile` | string | building name where recipe runs |
| `description` | string | shown in UI |
| `workload` | float | ticks to complete |
| `fuelCost` | float? | abstract fuel energy burned per round, satisfied by any `fuelValue>0` item (NOT a specific item — do not also list a fuel item in `ninputs`, or `Recipe.OnDeserialized` logs a double-charge error). `fuelCost 3` = 1 coal or 3 wood. Gated by `GlobalInventory.CanCraft`, reserved as a synthetic `CraftTask` fetch entry, shown as "fuel N" in the recipe panel. 0/absent = no fuel. |
| `research` | string? | technology node that receives passive progress on each completed cycle (maintain-only). Amount derives from `workload` (`PassiveCraftRate × workload`) — no separate points field; see SPEC-research |
| `skill` | string? | skill domain for XP (e.g. `"mining"`); defaults to `job.defaultSkill` if omitted |
| `maxRoundsPerTask` | int? | cap on rounds in one CraftTask trip (0/omit = unlimited). Set to 1 for "one item per trip" recipes (e.g. book writing) where each cycle should be a deliberate, discrete action rather than a batch. |
| `hidden` | bool? | omit from the Recipes panel (still craftable). Used for non-conventional pseudo-recipes like `dig` / `mine stone` whose "workstation" (digging pit, quarry) shouldn't appear as a recipe group. |
| `ninputs` | `[{name, quantity}]` | consumed items in liang |
| `noutputs` | `[{name, quantity, chance?}]` | produced items; `chance` (0–1) = probability of output |

**Recipes panel display notes:**
- The panel hides a recipe unless its **workstation is reachable** — research-unlocked OR currently placed (`ResearchSystem.IsBuildingUnlocked` ∥ `StructController.GetByType`) — and unless **every input item has been discovered** (`InventoryController.discoveredItems`, sticky + parent-chain-aware, so a group input like "wood" counts once any leaf appears). So e.g. the crucible's recipes and the oak-planks variant don't clutter the panel before you can make them. Filters live in `RecipePanel.IsWorkstationAvailable` / `InputsDiscovered`; the panel re-evaluates on every open.
- `description` should stay short — `Db.WarnLongRecipeNames()` logs a warning at load for any longer than the reference string `"smelt malachite into copper (wood-"` (34 chars), since long names truncate in the card header.
- Book-writing recipes (any recipe whose single output is `ItemClass.Book` — the runtime per-tech books + authored `fiction_book`) collapse in the panel into **one** generic "write a book" card per workstation; its On/Off toggles all book recipes together. See `RecipePanel.IsBookRecipe` / `BuildBookProxy`.

## Processor recipes (a `Recipe` with a `duration`)

A **processor recipe** is just an ordinary `Recipe` in `recipesDb.json` whose `tile` is a building with `hasProcessor` and which has a **`duration`** (seconds). `Recipe.isProcessorRecipe` is set at load (`= duration > 0`). Such recipes are **bucketed by building** in `Db.processorRecipesByBuilding` (`Db.GetProcessorRecipes(name)` returns the list) and kept OUT of `job.recipes`, so the craft dispatch never runs them as `CraftTask`s — the Processor's Fill/Work/Tap orders do (see SPEC-systems.md §Processors). `Db.ValidateProcessorRecipes()` checks every `hasProcessor` building has at least one. A building may host both kinds: the brewery's yeast recipe (`workload`, a craft) and its rice-wine recipe (`duration`, untended ferment) coexist.

Authored exactly like a craft `Recipe`, plus these processor-only fields:

| Field | Type | Notes |
|-------|------|-------|
| `duration` | float | seconds to complete one batch. UNTENDED = elapsed in-game seconds (temperature-scaled); TENDED = seconds of worker labour. Large for slow ferments (rice wine `960` = 2 in-game days × `ticksInDay`). Displayed via `Recipe.FormatDuration` — `<60s` as `"8s"`, else in-game days `"2 days"`. |
| `processTempMin`, `processTempIdeal` | float? | temperature ramp: rate 0 at/below min, 1.0 at/above ideal, linear between. Omit both → constant full rate. Read against AMBIENT weather (untended ferment). |
| `processColorHex` | string? | `#RRGGBB` tint for the building's `_w` liquid zone while Working. Absent → default blue. See SPEC-rendering.md §Decorative liquid zones. |

Inputs/outputs use the normal `ninputs`/`noutputs` (liang→fen). `fuelCost` is honoured for tended processors (hauled into the buffer, burned at tap). Recipe **selection** is the normal craft scorer, scoped to the building at fill time (`Animal.PickProcessorRecipe`).

**Recipes panel:** processor recipes are ordinary `Recipe`s, so they appear in their building's group alongside its craft recipes with no special pass. A processor card shows its batch time + ideal temp (e.g. `2 days at 25°`) in place of the worker-count line, and the standard per-recipe On/Off toggle (`RecipePanel.IsAllowed(id)`) — enforced by `PickProcessorRecipe` (a disabled recipe is skipped at fill time). No separate per-building process toggle.

## Foundry recipes (a `Recipe` with `foundryOp`)

The **foundry** is a melt pool, not a processor (SPEC-systems §Foundry). Its recipes carry `tile: "foundry"` and a **`foundryOp`** discriminator — `"melt"`, `"alloy"`, or `"cast"` — bucketed at load into `Db.foundryMeltRecipes` / `foundryAlloyRecipes` / `foundryCastRecipes`, kept OUT of both `job.recipes` and the processor bucket. Inputs/outputs use the normal `ninputs`/`noutputs` (liang→fen). The molten metals are real liquid `Item`s (`itemClass: "liquid"` + a `liquidColorHex`): `molten copper` / `tin` / `bronze` / `glass`.

| `foundryOp` | Shape | `foundryOp`-only fields |
|-------------|-------|--------------|
| `"melt"` | one ore (or a bar, for remelt) → one molten | `meltTempMin`, `meltTempIdeal` (rate ramp; UNCLAMPED below min so a cold pool re-solidifies), `meltDuration` (seconds per chunk — **size-independent**), `meltHeatCost` (heat per liang melted — latent heat) |
| `"alloy"` | molten + molten → molten (e.g. 1 copper + 1 tin → 2 bronze) | none — applied greedily in whole ratio-units, only when the cast target wants the product |
| `"cast"` | one molten → bars (1:1 fen by convention) | none — `research` gates it like any recipe |

Melt recipes are looked up by input item (`Db.GetFoundryMeltRecipe`), NOT scored. Cast recipes drive the cast-target picker (Auto scores them by output-bar scarcity — see SPEC-systems §Foundry).

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
| `usesTools` | bool? | whether animals on this job actively seek a tool to equip (default `true`). Tools only multiply work that routes through `ModifierSystem.GetWorkMultiplier` (gathering, crafting, research, construction), so purely-logistical jobs (hauler, merchant, runner) set `false`. A mouse reassigned off a tool-using job keeps any tool it already holds — the gate only stops the active seek (`Animal.FindEquipment`). |
| `defaultLocked` | bool? | hidden from the jobs panel until a gate is satisfied — a tech with a matching `{"type":"job"}` entry, or `unlockedByBuilding` |
| `unlockedByBuilding` | string? | one-way building gate: building type name whose first construction permanently reveals this job (independent of research). See SPEC-research §Job gating |
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
| `sunNeedDegrees` | float? | degrees of open overhead sky (of 180°) needed for full sun; default 90. Growth rate scales `clamp01(openSky/sunNeedDegrees)`, floored at 0.2 so deep shade slows not freezes. Lower = more shade-tolerant. See Plant Growth gate 1c in SPEC-systems. |
| `moistureDrawPerHour` | float? | passive draw from the soil tile below each in-game hour (default 1) |
| `stageMoistureCost` | int? | moisture deducted from the soil tile below each time the plant crosses into a new growth stage (default 4). Decoupled from `moistureDrawPerHour`; this is where moisture shortage actually gates growth — see gating below |
| `maxHeight` | int? | max tile-height this plant can reach (default 1). Multi-tile plants extend upward as growth stage crosses `growthStages`-stage thresholds; max stage = `growthStages × maxHeight − 1`. Yield at harvest scales linearly with the plant's current height. |
| `growthStages` | int? | growth-stage sprites per tile — the plant renders `g0..g{growthStages−1}`; default 4. Drives `maxStage`, the per-tile height step, and stage-sprite cycling. Herbs use 3 (g0/g1/g2). Ignored when `growthFrames` is present (table length wins). |
| `seasons` | string[]? | seasons this plant grows in (`"Spring"`/`"Summer"`/`"Fall"`/`"Winter"`, matching `WeatherSystem.GetSeason()`). Null/empty = year-round. A HARD growth gate like temperature: out-of-season freezes growth. `WildHerbSystem` also reads it to gate seasonal spawning + die-back. |
| `genWeight` | float? | relative weight for `WorldGen.ScatterPlants` (non-wild) **and** `WildHerbSystem` (wild) to pick this plant type. Unnormalized. Default 0 = never spawns naturally. |
| `maxWild` | int? | per-world live-population cap for a WILD herb (>0 marks the type wild). Wild types are world-spawned not player-planted, foraging *destroys* them instead of replanting, and `ScatterPlants` skips them — `WildHerbSystem` owns their lifecycle. Default 0 = normal crop/tree. |
| `placement` | string? | wild-herb terrain kind: `"meadow"` (surface dirt, default) or `"water"` (a lily — floats on the surface of a sky-exposed pond, no wind sway, no soil-moisture coupling; set `stageMoistureCost: 0`). |

For the gameplay mechanics behind these fields (comfort gating, per-hour moisture draw, stage-advance cost, height extension), see SPEC-systems.md §Plant Growth.

## `tilesDb.json` — TileTypes

Fields:

| Field | Type | Notes |
|-------|------|-------|
| `id` | int | unique (0=empty, 1=structure, 2=dirt, 3=sand, 4=clay, 20=limestone, 21=granite, 22=slate, 23=limestone_placed, 24=dirt_placed). Solid-tile ids drive the soft-edge sort ranking — lower id draws on top at different-type boundaries (see SPEC-rendering "Tile body sort order"). |
| `name` | string | internal lookup key — never shown to the player |
| `displayName` | string? | player-facing name in the info panel; falls back to `name` (see `TileType.DisplayName`). Lets a placed variant read as its base material — `limestone_placed` / `dirt_placed` both display as `limestone` / `dirt`. |
| `spriteName` | string? | **override** to borrow some *other* tile type's texture (not the `_placed` base). Rarely needed — `_placed` variants auto-borrow their base art. Resolution lives in `TileType.SpriteStem`, the single stem source shared by `TileSpriteCache` (final tile render) and `StructType.LoadSprite` (blueprint / build-ghost / menu icon), so all agree. |
| `_placed` convention | — | A player-built, non-harvestable variant of a base tile. Name it `<base>_placed`, set `displayName` = base, and **omit `group` (and any `nExtractionProducts`)** so a quarry/digging-pit can't target it. The sprite auto-borrows `<base>`'s via `SpriteStem` (no `spriteName` needed). `limestone_placed` = `limestone` minus group; `dirt_placed` = `dirt` (incl. its grass `overlay`) minus group — that's the *only* difference, blocking the digging pit and changing nothing else. |
| `solid` | int | 0=passable, 1=solid (blocks movement) |
| `group` | string? | logical family (e.g. `"stone"` for limestone/granite/slate, `"earth"` for dirt/sand/clay). `StructPlacement` treats `requiredTileName` as a match on either the tile's name or its group, so quarry's `requiredTileName: "stone"` accepts any stone variant and digging pit's `"earth"` accepts dirt/sand/clay. **Watch out**: name-only matches (e.g. burrow's `requiredTileName: "dirt"`) skip the group, so a burrow only digs into the dirt tile even though dirt is in the `"earth"` group. |
| `overlay` | string? | name of an overlay sprite sheet that tiles of this type can carry per-side decoration from. `dirt → "grass"` today; future moss-on-stone would set this on stone variants. Loads from `Resources/Sprites/Tiles/Sheets/<overlay>.png` (32×32 atlas, transparent Main interior). See SPEC-rendering "Tile overlays" for the rendering trick. |
| `nproducts` | `[{name, quantity}]`? | items dropped on tile break (semantically: "clear the area"). Simple flat drops, no chance. |
| `nExtractionProducts` | `[{name, quantity, chance?}]`? | items produced each craft cycle by an extraction building (quarry / digging pit) placed on this tile — by convention 1 liang of the base material plus chance-rolled rare finds. Distinct from `nproducts` because extraction is deliberate harvesting, not mining clearance. Consumed via `ExtractionBuilding.GetExtractionOutputs` → `AnimalStateManager` craft loop, and surfaced as the InfoPanel "yields" help hover on the uses line. Every tile an extraction building can target (groups `stone` and `earth`) must define it. |

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
