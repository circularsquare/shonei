# Shonei — Project Spec

## Overview

Shonei is a 2D tile-based colony management simulation (in the vein of Dwarf Fortress / RimWorld) where the player oversees a colony of autonomous mice. Players designate jobs, place buildings, and manage resources while mice carry out tasks on their own using a hierarchical AI system. Built in Unity with C#.

## Genre & Core Loop

- **Genre**: Colony sim / base builder
- **Perspective**: 2D side-view, tile-based (100×50 grid)
- **Core loop**: Assign jobs → mice carry out tasks → resources are gathered/processed → new buildings unlock → repeat

## Architecture

### Pattern: MVC + Singleton Controllers

```
World (singleton)
├── Tile[,] grid
├── AnimalController   → manages Animal instances
├── PlantController    → manages Plant instances
├── InventoryController → global item tracking
└── WorldController    → Unity rendering + input
```

All controllers are Unity MonoBehaviours. The `World` singleton provides access to all of them. Rendering is decoupled from model logic through callback registration (tiles fire callbacks on change).

### Directory Structure

```
Assets/
├── Controller/        Unity MonoBehaviours (UI, input, rendering)
├── Model/             Pure C# game logic
│   ├── World.cs       Tile grid, update loops, system access
│   ├── Animal.cs      Agent data + task dispatch
│   ├── AnimalStateManager.cs  State machine logic
│   ├── Task.cs        Task + Objective definitions
│   ├── Navigation.cs  A* pathfinding
│   ├── Inventory.cs   Item containers (animal/storage/floor)
│   ├── Plant.cs       Growing plants
│   ├── Structure.cs   Placed buildings
│   ├── Tile.cs        Grid cell
│   ├── Item.cs        Item type definitions
│   ├── Db.cs          JSON database loader
│   └── Reservable.cs  Resource reservation (capacity-based)
└── Resources/
    ├── buildingsDb.json
    ├── plantsDb.json
    └── recipesDb.json
```

### Update Frequencies

- **Animals + Plants**: every 1 second (game tick)
- **Inventory**: every 0.2 seconds
- **Rendering/Input**: every frame (Unity Update)

## Data-Driven Design

All game content is defined in JSON and loaded at startup via `Db.cs` using Newtonsoft.Json. String references (e.g., `"wood"`) are resolved to object references in `[OnDeserialized]` callbacks.

| File | Content |
|------|---------|
| `buildingsDb.json` | StructTypes: house, drawer, sawmill, soil pit, ladder, stairs, platform |
| `plantsDb.json` | PlantTypes: tree (30 ticks), wheat (15 ticks) |
| `recipesDb.json` | Recipes: sawyer (wood → plank + sawdust), digger (∅ → soil) |

Lookups: `itemByName`, `jobByName`, `structTypeByName`, `plantTypeByName`, `tileTypeByName`

## Save / Load / Reset

World state is serialized to JSON via Newtonsoft.Json and stored in `Application.persistentDataPath/saves/<slot>.json`. The entry point is `SaveSystem` (MonoBehaviour singleton).

### Save data classes (`WorldSaveData.cs`)
Plain C# classes — **no `[Serializable]`** (not needed by Newtonsoft.Json; adding it causes Unity's own serializer to materialize default instances on MonoBehaviour fields instead of null).

### Structure creation rules
Two legitimate ways to put a structure into the world:

| Method | When to use |
|--------|-------------|
| `StructController.Construct(st, tile)` | Normal gameplay (via `Blueprint.Complete()`). Deducts costs from `GlobalInventory`. |
| `new Building/Plant/etc.(…)` + `StructController.Place(s)` | Load path and world generation. No cost side-effects. |

Always call `Place()` after direct construction — it registers the structure for tracking so `ClearWorld()` can find and destroy it later. Direct `new X()` without `Place()` is a bug.

### Startup ordering

Unity `Start()` execution order between MonoBehaviours is undefined. `WorldController.Start()` is an `IEnumerator` that `yield return null`s before calling `GenerateDefault()`, ensuring all other `Start()` methods (notably `AnimalController.Start()` initializing `jobCounts`) have run first. After `GenerateDefault()`, it starts `SaveSystem.instance.PostLoadInit()` — the same coroutine used by Load and Reset — so colony stat initialization runs consistently on all three paths.

### Initial / Reset / Load flow

```
Initial: WorldController.GenerateDefault()  →  PostLoadInit (next frame)
Reset:   WorldController.ClearWorld()  →  WorldController.GenerateDefault()  →  PostLoadInit (next frame)
Load:    WorldController.ClearWorld()  →  WorldController.ApplySaveData(data)  →  PostLoadInit (next frame)
```

**`ClearWorld()`** tears down in this order:
1. Destroy all structures via `StructController.GetStructures()` → `s.Destroy()` (nulls tile refs, removes from PlantController/StructController, destroys GOs)
2. Destroy all blueprints (iterate tiles, call `bp.Destroy()`)
3. Destroy all animals (call `animal.Destroy()` which destroys their inventory + GO), reset `na = 0`, call `ResetJobCounts()`
4. Destroy remaining inventories (tile floor/storage invs) via `InventoryController.inventories`
5. Zero all `GlobalInventory.itemAmounts`
6. Reset all tiles: null `tile.inv`, set `tile.type = empty` (fires sprite callbacks)
7. Reset `world.timer`, clear `InfoPanel`

**`GenerateDefault()`** rebuilds:
1. Set tile types (soil y<10, stone y<8)
2. Create default plants + `Place()` each with StructController
3. `world.graph.Initialize()`
4. Spawn 4 animals via `AnimalController.AddAnimal()`
5. `StartCoroutine(DefaultJobSetup)` — yields 1 frame (waits for `Animal.Start()`), then assigns jobs and starting wheat

**`ApplySaveData()`** rebuilds from file:
1. Apply tile types + blueprints + structures (via `Place()`) + inventories per saved tile
2. `world.graph.Initialize()`
3. `LoadAnimal()` per saved animal (sets `pendingSaveData`; `Animal.Start()` applies it next frame)

`SaveSystem.PostLoadInit()` is started as a coroutine at the end of all three paths. It yields one frame (waiting for any `Animal.Start()` calls spawned this frame to complete), then calls `AnimalController.instance.Load()`. This is the correct place to run any initialization that depends on animals being fully ready (e.g. `SlowUpdate()` to initialize happiness, `UpdateColonyStats()`).

### `pendingSaveData` pattern
`LoadAnimal()` sets `animal.pendingSaveData` before `Animal.Start()` runs. `Start()` checks it: if set, applies saved stats/job; if null, initializes with full hunger/sleep. Marked `[System.NonSerialized]` to prevent Unity from replacing null with a default instance.

### Timing rules: when can you call what?
| Moment | What has run | What has NOT run yet |
|--------|-------------|----------------------|
| Inside `ApplySaveData()` | Tiles, structures, nav graph | `Animal.Start()` (next frame) |
| Inside `Animal.Start()` (pendingSaveData branch) | `world`, `nav`, `eating`, `eeping`, `happiness` all initialized | `FindHome()` not yet called (no homeTile) |
| `PostLoadInit` coroutine (1 frame after load) | All `Animal.Start()` calls | — safe to call `SlowUpdate()`, `UpdateColonyStats()` |
| `AnimalController.TickUpdate` `if (world == null)` block | First game tick after scene load | — `world` is never null after a reload, so this only fires on a fresh scene launch; superseded by `PostLoadInit` |

**Key rule**: use `PostLoadInit` for all post-spawn initialization — it runs on every path (initial, Reset, Load). Do NOT use the `if (world == null)` guard in `AnimalController.TickUpdate`; `world` is never reset to null on reload so that block is unreliable across all paths.

## Animal AI

### States

```
Idle → Working
     → Moving → (arrives) → back to Working or Idle
Idle → Eeping (sleeping)
```

- **Idle**: calls `ChooseTask()`, selects best recipe by score
- **Working**: executes current objective (craft, harvest, build, sleep)
- **Moving**: navigates path via A*; calls `OnArrival()` on completion
- **Eeping**: sleeps at home, restores sleep meter; can trigger reproduction

### Needs

| Need | Effect |
|------|--------|
| Hunger | Reduces efficiency; eating wheat restores |
| Sleep | Reduces efficiency; sleeping at home restores |
| Efficiency | `eating.Efficiency() * eeping.Efficiency()` — scales move speed and work rate |

### Task System

Tasks decompose into an ordered queue of Objectives. Each task:
1. **Initialize** — validates feasibility, creates objective queue, reserves resources
2. **Execute** — runs objectives sequentially
3. **Complete** — cleanup, return to Idle
4. **Fail** — release reservations, return to Idle

**Tasks (implemented):**

| Task | Description |
|------|-------------|
| `CraftTask` | Navigate to station, fetch inputs, work, drop outputs |
| `HarvestTask` | Navigate to plant, harvest when ready, drop products |
| `HaulTask` | Move floor/excess items to proper storage |
| `ConstructTask` | Build a blueprint |
| `SupplyBlueprintTask` | Deliver resources to incomplete blueprint |
| `ObtainTask` | Fetch a specific item |
| `EepTask` | Navigate home and sleep |
| `DropTask` | Drop excess inventory |
| `GoTask` | Navigate to a tile |
| `FallTask` | Fall downward until stable |

**Objectives (atomic steps):**
`GoObjective`, `FetchObjective`, `DeliverObjective`, `WorkObjective`, `EepObjective`, `FallObjective`

### Job System

Each animal has one Job. Jobs define which Recipes the animal can execute. Recipe selection uses a score that balances global item quantities against configurable targets.

## Navigation

- **Algorithm**: A* with Manhattan heuristic
- **Standability**: tile is standable if tile below is solid, has a platform/building, or has a ladder/stairs
- **Vertical movement**: ladders (straight up), stairs (diagonal)
- **Helper queries**: `FindPathToBuilding`, `FindPathToItem`, `FindPathToStorage`, `FindPathAdjacentToBlueprint`, `FindPathToHarvestable`

## Inventory System

Three inventory types:

| Type | Slots | Stack Size | Decay Rate |
|------|-------|-----------|-----------|
| Animal | 5 | 10 | normal |
| Storage | varies | varies | normal |
| Floor | 5 | varies | 5× normal |

- Items decay over time 
- `allowed` dict filters what item types a storage accepts
- `Reservable` (capacity-based) prevents multiple animals targeting same resource
- `Produce()` adds to inventory and global inventory simultaneously

## Rendering & Layers

Structures render in three depth layers per tile:
- **Background (b)**: buildings, plants
- **Midground (m)**: platforms
- **Foreground (f)**: stairs, ladders

This enables stairs and multi-part structures to be placed on the same tiles

## Current State & Known Issues

### In Progress

- **Reservation system**: partially implemented; needs extending to crafting, obtaining tasks
- **Multi-round work**: `WorkObjective` needs to span multiple ticks (affects crafting and harvesting)
- **Blueprint inventories**: need allow-lists for specific resource requirements
- **Blueprint placement**: must not override floor items; overlapping blueprints need conflict detection
- **Animal AI**: prevent infinite job-switching; add leisure state

### Known Bugs

- Building on tile with floor item can delete the item
- Screen jitters when panning
- Mice can get stuck descending slopes
- Stairs don't position animals correctly
- Digger job sometimes loops
- Blueprint overlap validation missing

### Planned Content

- Quarry building + sprites
- Mining floor tiles (separate command)
- Plant moisture/temperature mechanics
- More jobs and recipes

## Key Design Decisions

- **Hierarchical tasks over behavior trees**: simple objective queue is easy to reason about and extend
- **Data-driven content**: adding new items/buildings/recipes requires no code changes
- **Global inventory tracking**: enables recipe scoring without querying every tile
- **Floor decay penalty**: incentivizes hauling without enforcing it
- **Reservation before execution**: tasks reserve resources during Initialize to prevent conflicts at runtime

## Multiplayer Trading

Player-to-player marketplace using a WebSocket connection to a separate Go
server. Players can post buy/sell orders, view live order books, execute trades,
and chat with other settlements.

### Architecture

```
Unity Client (TradingClient.cs)
        │  WebSocket  ws://127.0.0.1:8080/ws?name=<PlayerName>
        ▼
  Go Server  (~/projects/shonei-server/main.go)
    └── Hub  (goroutine, central coordinator)
          ├── per-client readPump  (goroutine)
          ├── per-client writePump (goroutine)
          └── Exchange
                └── Book per item  (in-memory, price-time priority)
```

- Hub pattern: all clients communicate through the central Hub; clients never
  address each other directly.
- Player name passed as a query param on connect (`?name=Mouse1`); the server
  stamps it onto outgoing chat and order messages.
- Unity client auto-reconnects every 20 s when disconnected.
- Server binds to `127.0.0.1:8080` (localhost only for now).

### Wire Protocol

All messages use an envelope wrapper:

```json
{ "type": "<message_type>", "payload": { ... } }
```

#### Client → Server

| Type | Purpose | Key fields |
|---|---|---|
| `order` | Place a buy or sell order | `item`, `side` ("b"/"s"), `price`, `quantity` |
| `market_query` | Request the current order book for an item | `item` |
| `chat` | Send a chat message to all players | `text` |

`from` is always injected server-side from the connection name.

#### Server → Client

| Type | Sent to | Purpose | Key fields |
|---|---|---|---|
| `market_response` | Requester only | Order book snapshot | `item`, `buys[]`, `sells[]` |
| `fill` | All clients | Trade executed | `buyer`, `seller`, `item`, `price`, `quantity` |
| `order` | All clients | Order placement broadcast | `from`, `item`, `side`, `price`, `quantity` |
| `chat` | All clients | Chat message | `from`, `text` |

`buys[]` is sorted highest price first (best bid at index 0).
`sells[]` is sorted lowest price first (best ask at index 0).
Fill price is the **resting order's price** (maker price).

### Order Book — Matching Engine

Continuous double auction, price-time priority:

1. Incoming **buy** at price P matches resting sells with `price ≤ P`, best ask
   first.
2. Incoming **sell** at price P matches resting buys with `price ≥ P`, best bid
   first.
3. Fill quantity = `min(incoming.qty, resting.qty)`.
4. After matching, any remaining quantity rests in the book (no expiry).
5. All fills broadcast to all clients.

Insertion maintains sorted order via binary search.

### Unity Client — TradingClient.cs

Singleton (`TradingClient.instance`). Attach to a persistent GameObject.

**Events:**

| Event | Fired when |
|---|---|
| `OnConnectionChanged` | WebSocket connect / disconnect |
| `OnMarketResponse` | Server sends order book snapshot |
| `OnFill` | A trade executes (broadcast to all) |
| `OnChat` | Chat message received |

**Public methods:**

```csharp
TradingClient.instance.QueryMarket(string item);
TradingClient.instance.SendOrder(string item, string side, int price, int qty);
TradingClient.instance.SendChat(string text);
```

All methods are no-ops when offline.

### Unity UI — TradingPanel.cs

Singleton. Subscribes to TradingClient events in `Start()`, unsubscribes in
`OnDestroy()`.

**Inspector fields:**

| Field | Type | Purpose |
|---|---|---|
| `itemInput` | TMP_InputField | Item name (shared by query and order entry) |
| `buysList` | Transform | Scroll content for buy-side book rows |
| `sellsList` | Transform | Scroll content for sell-side book rows |
| `orderPrice` | TMP_InputField | Price input for order entry |
| `orderQty` | TMP_InputField | Quantity input for order entry |
| `chatScroll` | ScrollRect | Wraps chatList; used for auto-scroll to bottom |
| `chatList` | Transform | VLG content for chat + fill messages |
| `chatInput` | TMP_InputField | Chat message input |
| `onlineIndicator` | GameObject | Image + TMP child showing online/offline |

**Button wiring:**

| Button | Method |
|---|---|
| TradingToggle | `TradingPanel.instance.Toggle()` |
| QueryButton | `TradingPanel.instance.OnClickQuery()` |
| BuyButton | `TradingPanel.instance.SetBuy(true)` |
| SellButton | `TradingPanel.instance.SetBuy(false)` |
| PlaceOrderButton | `TradingPanel.instance.OnClickPlaceOrder()` |
| ChatSendButton | `TradingPanel.instance.OnClickSendChat()` |

`itemInput` and `chatInput` also trigger on Enter key (wired in `Start()`).

**Chat/fill display:** capped at 20 entries; fills shown in green. Requires
`chatList` VerticalLayoutGroup with **Control Child Size Width ON, Height OFF**;
each row uses ContentSizeFitter (vertical = PreferredSize).

**Indicator sprites:** loaded from `Resources/Sprites/Misc/indicator/green` and
`Resources/Sprites/Misc/indicator/red`.

### Running the Server

```bash
cd ~/projects/shonei-server
go mod tidy      # first run only
go run main.go   # listens on 127.0.0.1:8080
```

Test client: `go run client/main.go -name=Mouse1`
Commands: `/b <item> <qty> <price>`, `/s <item> <qty> <price>`, `/market <item>`, or plain text for chat.

### Trading Logistics (in-game item flow)

Trades don't happen directly on tiles — all goods and silver pass through a
special **Market building**, which represents a distant city.

#### Market building

- StructType `"market"`, `isMarket = true` on the StructType.
- Auto-spawned once at world gen at tile `(10, 10)`; never player-buildable.
- Does **not** appear in the build menu (enforced via the unlock system — see below).
- The Market inventory is a normal `Inventory` but tagged `isMarket = true`.
  Normal haul logic (HaulTask) skips market inventories; only merchant mice
  may target them.

#### Player name

`TradingClient.playerName` — hardcoded `"anita"` for now. Used to identify
which side of a fill belongs to this player.

#### Merchant job

Dedicated `"merchant"` job in jobsDb. Merchant mice only perform
`HaulToMarketTask` and `HaulFromMarketTask`; they do not take craft/harvest
jobs. They are the only mice allowed to path to the market building.

#### Order placement flow

Before calling `TradingClient.SendOrder`, `TradingPanel` validates against the
market inventory:

- **Sell order** (item → silver): market inv must have ≥ `qty` of `item`
  unreserved.
- **Buy order** (silver → item): market inv must have ≥ `price × qty` silver
  unreserved, **and** enough unreserved incoming capacity for `qty` of `item`.

If checks pass:
1. Send order to server.
2. For sells: reserve `qty` of `item` in market inv (existing `Reservable`
   mechanism).
3. For buys: reserve `price × qty` silver outgoing **and** reserve `qty`
   incoming capacity for `item` (new `reservedIncoming` dict on Inventory —
   see below).

#### Fill processing

When `OnFill` fires and `fill.buyer == playerName` or `fill.seller ==
playerName`:

- **We are buyer**: deduct `fill.price × fill.quantity` silver from market
  inv; add `fill.quantity` of `fill.item` to market inv. Release silver
  outgoing reservation and incoming capacity reservation for item.
- **We are seller**: deduct `fill.quantity` of `fill.item` from market inv;
  add `fill.price × fill.quantity` silver to market inv. Release item
  outgoing reservation and incoming capacity reservation for silver.

Partial fills are handled — only the filled quantity is deducted/released.

#### Space (incoming capacity) reservation

New field on `Inventory`: `Dictionary<Item, int> reservedIncoming`.

- Counts how many units of each item are pre-allocated for pending buy orders.
- Available incoming capacity for item X = `(totalSlots - usedSlots) -
  reservedIncoming[X]`.
- Initially only used for the market inventory; can generalize later.

#### Building unlock system

New field on `StructType` in buildingsDb.json: `"defaultUnlocked"` (bool,
defaults to `false` if absent → **unlocked**). Wait — to keep existing
buildings unlocked by default, we should default to `true`. So:

- `"defaultUnlocked": true` → available from the start (all current buildings)
- `"defaultUnlocked": false` (or absent) → locked; hidden from build menu
- The Market StructType has `"defaultUnlocked": false` (not player-buildable).
- `World` (or a future `ResearchController`) tracks a `HashSet<string>
  unlockedBuildings`. `BuildPanel` only shows entries present in that set.

#### Known gaps / TODO

- **Concurrency**: `Exchange.placeOrder` is called from per-client goroutines
  with no mutex on the Exchange — needs to be serialized through the Hub's
  `run()` goroutine.
- **Order cancellation**: not yet implemented on server; resting-order
  reservations cannot be released until this is added.
- **Persistence**: order book is in-memory only; lost on server restart.
- **Player name**: hardcoded as `"anita"`; make configurable later.
- **Authentication**: none; name is trusted from query param.
- **LAN/internet play**: change server bind to `0.0.0.0` and update `WsUrl`.
- **NPC / bot orders**: no server-side liquidity seeding.
- **Redundant order broadcast**: the `order` broadcast after matching is noisy
  since clients already receive `fill` messages; consider removing.

## Technology

- **Engine**: Unity (2D)
- **Language**: C#
- **JSON**: Newtonsoft.Json (with `[OnDeserialized]` reference resolution)
- **Pathfinding**: Custom A* in `Navigation.cs`
- **Sprites**: Custom pixel art (mouse, plants, buildings)
- **Trading server**: Go, gorilla/websocket, in-memory order book
