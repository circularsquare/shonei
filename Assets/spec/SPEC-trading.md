# Shonei — Multiplayer Trading

Player-to-player marketplace using a WebSocket connection to a separate Go
server. Players can post buy/sell orders, view live order books, execute trades,
and chat with other settlements.

## Architecture

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

## Wire Protocol

All messages use an envelope wrapper:

```json
{ "type": "<message_type>", "payload": { ... } }
```

### Client → Server

| Type | Purpose | Key fields |
|---|---|---|
| `order` | Place a buy or sell order | `item`, `side` ("b"/"s"), `price`, `quantity` |
| `market_query` | Request the current order book for an item | `item` |
| `chat` | Send a chat message to all players | `text` |

`from` is always injected server-side from the connection name.

### Server → Client

| Type | Sent to | Purpose | Key fields |
|---|---|---|---|
| `market_response` | Requester only | Order book snapshot | `item`, `buys[]`, `sells[]` |
| `fill` | All clients | Trade executed | `buyer`, `seller`, `item`, `price`, `quantity` |
| `order` | All clients | Order placement broadcast | `from`, `item`, `side`, `price`, `quantity` |
| `chat` | All clients | Chat message | `from`, `text` |

`buys[]` is sorted highest price first (best bid at index 0).
`sells[]` is sorted lowest price first (best ask at index 0).
Fill price is the **resting order's price** (maker price).

## Order Book — Matching Engine

Continuous double auction, price-time priority:

1. Incoming **buy** at price P matches resting sells with `price ≤ P`, best ask
   first.
2. Incoming **sell** at price P matches resting buys with `price ≥ P`, best bid
   first.
3. Fill quantity = `min(incoming.qty, resting.qty)`.
4. After matching, any remaining quantity rests in the book (no expiry).
5. All fills broadcast to all clients.

Insertion maintains sorted order via binary search.

## Unity Client — TradingClient.cs

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

## Unity UI — TradingPanel.cs

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

**Chat/fill display:** the chat list is a *view* over `EventFeed` — TradingPanel subscribes to `EventFeed.OnEntry` in `Awake` and renders every entry as a chat row (capped at 20 visible rows). Market errors, `/give` feedback, server chat, and trade fills all flow through `EventFeed.Post(...)` rather than writing to the chat list directly. Inline `<color=...>` tags on the posted text still drive per-message coloring (green for fills and `/give` success, red for errors). See SPEC-eventfeed for the dispatcher contract.

**Indicator sprites:** loaded from `Resources/Sprites/Misc/indicator/green` and
`Resources/Sprites/Misc/indicator/red`.

## Running the Server

```bash
cd ~/projects/shonei-server
go mod tidy      # first run only
go run main.go   # listens on 127.0.0.1:8080
```

Test client: `go run client/main.go -name=Mouse1`
Commands: `/b <item> <qty> <price>`, `/s <item> <qty> <price>`, `/market <item>`, or plain text for chat.

## Trading Logistics (in-game item flow)

Trades don't happen directly on tiles — all goods and silver pass through a
special **Market building**, which represents a distant city.

### Market building

- StructType `"market"`, `isMarket = true` on the StructType.
- Auto-spawned once at world gen at tile `(0, 20)` (left world edge, off normal camera view); never player-buildable. Represents a distant city.
- Does **not** appear in the build menu (enforced via the unlock system — see below).
- The Market inventory starts as `InvType.Storage` then `SetMarket()` flips it to `InvType.Market` and initializes `targets`/`incomingRes`. Normal haul logic (HaulTask) skips market inventories; only merchant mice may target them.

### Market targets are leaf-only

`marketInv.targets` is seeded from `Db.itemsFlat`, which contains both group items (e.g. "wood") and leaf items (e.g. "oak"). Only **leaf** targets are meaningful — groups are wildcards for recipe/building inputs and never appear as physical stacks in an inventory (see "Group items are never physical" in CLAUDE.md).

Consequences:
- **UI**: `TradingPanel` market tree always renders groups expanded and hides the target +/-/text widgets on group rows (`ItemDisplay.SetDisplayMode` in Market mode). Clicking the dropdown on a group is a no-op in Market mode.
- **Model**: `HaulToMarketTask` / `HaulFromMarketTask` skip `kvp.Key` entries whose item has children. `WorkOrderManager.MarketNeedsHaulTo` / `MarketNeedsHaulFrom` apply the same leaf-only filter so a group's default 0-target can't spuriously fire orders against summed child quantities.
- **Data**: group keys stay in the `targets` dict (with value 0) for save-compatibility; nothing reads or writes them.

### Travel mechanic

Merchants walk to the market tile at x=0 (the "portal"), then disappear for a
transit period representing the journey to/from the distant city. Both market
tasks take **two** transit legs: outbound (town → market) and return
(market → town). For `HaulToMarket` the inventory transfer happens between the
two legs (at the market); for `HaulFromMarket` it happens between the two legs
as well (receive at market, then walk to home storage after return).

- `TravelingObjective(durationTicks)` hides the animal (`go.SetActive(false)`)
  and sets `AnimalState.Traveling`. `AnimalStateManager.HandleTraveling()` ticks
  `workProgress` until the duration is reached, then reappears the animal.
- Transit duration: `World.ticksInDay / 12` per leg (~20s real time at 1 tick/sec), same both ways (`Task.MarketTransitTicks`).
- `Nav.FindMarketPath()` uses r=120 (market at x=0 is outside the default r=40
  search range).
- Save/load: mid-transit merchants persist the full task context on
  `AnimalSaveData` — `isTraveling`, `travelProgress`, `travelDuration` plus a
  task descriptor (`travelTaskType`, `travelItemName`, `travelItemQty`,
  `travelStorageX/Y` for HaulFromMarket, `travelReturnLeg` indicating whether
  the merchant was still outbound or already homeward-bound). `travelReturnLeg`
  applies to both `HaulToMarket` (true once delivered, on leg 2) and
  `HaulFromMarket` (true once items received, on leg 2). On load
  `Animal.Start()` reconstructs the correct task via resume-mode constructors
  on `HaulToMarketTask` / `HaulFromMarketTask`, which rebuild the tail of
  objectives (the unfinished `TravelingObjective` at the canonical full
  `MarketTransitTicks` duration, then deliver/receive + return-leg objectives
  as appropriate), skipping the eep/food gates and the outbound pathing that
  already happened pre-save, and re-issue the market/storage space
  reservations — reservations themselves are never persisted, so every task
  restores its own on load. **Progress is tracked in
  one place**: `animal.workProgress` is the single source of truth. After
  `task.Start()` zeroes it (via `TravelingObjective.Start()`), Animal.Start
  restores `workProgress = pendingSaveData.travelProgress`, so the
  MerchantJourneyDisplay icon resumes at the correct fraction of the strip
  instead of restarting at 0% over a shortened objective. If the market or
  destination storage was demolished between save and load the resume
  constructor returns false and the animal falls back to a bare
  `ResumeTravelTask` (finish the tail-only remaining ticks, go idle, drop
  items via normal idle behaviour). Legacy saves without the descriptor also
  fall through to `ResumeTravelTask`.

### Journey display UI

`MerchantJourneyDisplay` (child strip inside TradingPanel) renders a head icon
for every animal currently in `AnimalState.Traveling`. Icons lerp along a
horizontal line between two RectTransform anchors — `marketAnchor` (left, the
distant city) and `townAnchor` (right, home) — based on the merchant's progress
through its `TravelingObjective`.

Each icon is a `MerchantJourneyIcon`: an `Image` with an `IPointerClickHandler`
that routes clicks to `InfoPanel.ShowInfo(new List<Animal>{ a })`. Icons are
constructed in code via `MerchantJourneyIcon.Create(...)` (no prefab) and copy
`sprite` + `color` from the animal's `Head` child SpriteRenderer, so per-mouse
sprite/colour variation carries into the strip. `IPointerClickHandler` is
preferred over `Button` to avoid unused transition/interactable machinery.

Direction of travel is inferred from the animal's task:
- `HaulToMarketTask` — first leg outbound, second leg returning. Detected via
  `IsReturnLeg`: once no `DeliverToInventoryObjective` remains in the queue,
  the merchant has handed over the goods and is on the homeward `TravelingObjective`.
- `HaulFromMarketTask` — first leg outbound, second leg returning. Detected via
  `IsReturnLeg`: once no `ReceiveFromInventoryObjective` remains in the queue,
  the merchant has picked up the goods and is on the homeward leg.
- `ResumeTravelTask` — legacy fallback only (old saves or when market/storage
  was demolished). Direction isn't persisted, falls back to outbound. Normal
  loads rebuild a proper `HaulToMarketTask` / `HaulFromMarketTask` so direction
  detection works exactly as in live play.

The display self-ticks from its own `Update` whenever the panel is visible; no
wiring from TradingPanel is needed beyond the inspector reference.

### Player name

`TradingClient.playerName` — hardcoded `"anita"` for now. Used to identify
which side of a fill belongs to this player.

### Merchant job

Dedicated `"merchant"` job in jobsDb. Merchant mice only perform `HaulToMarketTask` and `HaulFromMarketTask`; they do not take craft/harvest jobs. They are the only mice allowed to path to the market building.

`UpdateMarketOrders(marketInv)` adds a `HaulToMarket` order at **priority 3** when the market has any item below target, and a `HaulFromMarket` order at **priority 4** when any item is above target. The tier split encodes a dispatch preference: merchants exhaust outbound delivery work before considering a pure pickup trip. Combined with the piggyback described below, most excess gets hauled back on the return leg of a delivery, and a pure `HaulFromMarket` only fires when there is genuinely nothing to deliver. Merchants are the only job whose `canDo` matches these orders, so sharing the p4 tier with Research/Deconstruct is collision-free. Orders are deduped by market inventory reference. Registration happens in `Reconcile()` (every 10 s), on inventory changes, and after each task finishes.

**Dispatch gate — target edit delay:** both `HaulToMarket` and `HaulFromMarket` orders are suppressed for 3 s after the player manually edits a market target (`Inventory.lastTargetManualUpdateTimer`, gated by `marketHaulDelayAfterTargetChange`). Applied to both directions because an edit can flip an item from below-target to above-target or vice versa; letting one direction fire immediately would dispatch a merchant on a soon-to-be-stale target.

**Task initialization gates (both market tasks):**
- Eep < 75 %: merchant refuses the trip (`animal.eeping.Eepness() < 0.75f`). Stricter than the general night-sleep threshold so a merchant doesn't dip into efficiency-loss territory mid-transit.
- Food provisioning: `PrependFoodFetchForMarketJourney(transitTicks, extraGroundSeconds=0)` requires `foodNeeded = hungerRate × (2 × (WalkToPortalSeconds=20 + transitTicks) + extraGroundSeconds) + maxFood × hungryThreshold`. Read this as "enough to burn across the round trip **and** land home exactly at the hungry threshold line". `transitTicks` is **per leg** — the `2×` factor covers the round trip. `extraGroundSeconds` budgets any trip-terminating on-map walk beyond the portal (used by the piggyback path, which ends at a specific storage tile instead of idle at x=0). It checks body food + food slot, then fetches from the nearest available food source into the food slot if the deficit can be covered. Returns false (aborts Initialize) if it cannot provision enough food. `HaulFromMarket` passes `(MarketTransitTicks)`; `HaulToMarket` passes `(MarketTransitTicks, WalkToPortalSeconds)` when a piggyback is planned, else `(MarketTransitTicks)`.
- Minimum haul quantity: `MinMarketHaulQuantity = 100 fen` (1.0 liang) for most items; `MinMarketHaulQuantitySilver = 40 fen` (silver moves in smaller amounts). Selected via `MinMarketHaul(item)`.

### Return-leg piggyback (`HaulToMarket` only)

When a `HaulToMarketTask` initializes, after building its outbound deliver queue it calls `TryAppendPickup(marketInv)`. If there is an active `HaulFromMarket` order for the same market **and** a leaf item above target with ≥ `MinMarketHaul` units of unreserved excess and a reachable home storage with room, the pickup is spliced into the trip and the objective queue becomes:

```
Fetch (food) → Go (portal) → Travel (outbound)
  → Deliver (market)
  → Receive (market)                   ← piggyback tail begins
  → Travel (return)
  → Go (home storage)
  → Deliver (home storage)
```

`TryAppendPickup` reserves the market source stack + home storage space and claims the `HaulFromMarket` WOM order's `res` so a second merchant doesn't race the pickup. The claimed order is released in `HaulToMarketTask.Cleanup()` (override), alongside the normal stack/space reservation unwind in `base.Cleanup()`. If no viable pickup is found, the task behaves identically to its pre-piggyback form (plain return travel, merchant reappears idle at x=0).

`IsReturnLeg` is defined in terms of the market delivery objective specifically (`DeliverToInventoryObjective.TargetInv == marketInv`) so it stays correct when the queue contains two `DeliverToInventoryObjective`s (market + home). `PickupReceived` is true once the `ReceiveFromInventoryObjective` has completed — used by the save/load mapping below.

**Save/load mapping (no schema change).** Because the piggyback tail becomes structurally identical to a `HaulFromMarketTask` return leg once pickup items are on board, we reuse the existing `HaulFromMarket` save descriptor to represent that phase. Phase detection in `SaveSystem.GatherAnimal`:

| Phase at save (merchant is in `AnimalState.Traveling`) | Descriptor emitted | Resume behaviour |
|---|---|---|
| Outbound travel (pre-market-deliver) | `HaulToMarket`, `returnLeg=false`, primary `iq` | `HaulToMarketTask` outbound resume. Planned pickup silently dropped — we lose an opportunistic trip, not a committed task. |
| Return travel, no pickup | `HaulToMarket`, `returnLeg=true` | `HaulToMarketTask` return resume — tail is just remaining travel. |
| Return travel, pickup received | `HaulFromMarket`, `returnLeg=true`, pickup `iq` + storage tile | `HaulFromMarketTask` return resume — tail is remaining travel + go-to-storage + deliver. Primary goods were delivered pre-save, so no drift. |

Rough edge: if the game is saved during the brief non-Traveling window at the market (between `DeliverToInventoryObjective` and `ReceiveFromInventoryObjective`), `SaveSystem` skips the descriptor (it's only emitted for `AnimalState.Traveling`) and the task is dropped on load. The merchant wakes idle at the market tile; the next WOM pass dispatches a fresh `HaulFromMarket` if excess remains, otherwise the merchant walks home.

### Order placement flow

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

### Fill processing

When `OnFill` fires and `fill.buyer == playerName` or `fill.seller ==
playerName`:

- **We are buyer**: deduct `fill.price × fill.quantity` silver from market
  inv; add `fill.quantity` of `fill.item` to market inv. Release silver
  outgoing reservation and incoming capacity reservation for item.
- **We are seller**: deduct `fill.quantity` of `fill.item` from market inv;
  add `fill.price × fill.quantity` silver to market inv. Release item
  outgoing reservation and incoming capacity reservation for silver.

Partial fills are handled — only the filled quantity is deducted/released.

### Space (incoming capacity)

`Inventory.GetStorageForItem(item)` returns available space for an item,
accounting for in-flight delivery reservations (`resSpace`). TradingPanel uses
this to validate buy/sell orders before sending to the server. No client-side
order reservation is tracked — if the market fills between placement and fill
arrival, excess goods are simply rejected or overflow.

### Building unlock system

`StructType` has a `defaultLocked` bool (JSON field `"defaultLocked": true`).

- Absent/false → available in build menu from the start (most buildings).
- `true` → hidden from build menu at startup; must be unlocked via research.
- Currently locked: `soil pit`, `quarry`, `market` (market is never unlockable — it's auto-spawned by world gen).
- `BuildPanel.Start()` skips locked buildings when building sub-panels.
- `BuildPanel.UnlockBuilding(name)` adds the entry to the correct sub-panel at runtime, called from `ResearchSystem.ApplyEffect`.

## Foreign Traders

Foreign nations (e.g. Fulan) are `DynamicTrader` instances running entirely
server-side in `shonei-server/bots.go` with direct exchange access. Each trader
manages one item and maintains dynamic buy/sell prices based on internal stock.

**Pricing:**
```
sellPrice = clamp(defaultPrice × defaultStock / stock + minPrice, _, maxPrice)
buyPrice  = sellPrice / 2
```

**Stock dynamics:** `startFarming()` ticks every 10 s, adjusting stock toward
`defaultStock`. Low stock → gains; excess stock → losses. Max gain/loss per tick
configurable per trader.

**Order refresh:** `refreshOrders()` calls `cancelOrdersForItem(name, item)` (scoped
to this trader's item only — important when a nation has multiple traders) then
re-places buy/sell orders at current prices. Called on fill and on each farming tick.

**Config:** `traders.json` — one entry per `DynamicTrader`. Multiple entries can
share the same `name` for a nation that trades multiple items.

**To add a new nation:** add an entry to `traders.json`.  
**To add more items for a nation:** add another entry with the same `name`.  
**Prices/quantities:** always in fen (100 fen = 1 liang).

### Known gaps / TODO

- **Concurrency**: `Exchange.placeOrder` is called from per-client goroutines
  with no mutex on the Exchange — needs to be serialized through the Hub's
  `run()` goroutine.
- **Order cancellation**: not yet implemented on server; resting-order
  reservations cannot be released until this is added.
- **Persistence**: order book is in-memory only; lost on server restart.
- **Player name**: hardcoded as `"anita"`; make configurable later.
- **Authentication**: none; name is trusted from query param.
- **LAN/internet play**: change server bind to `0.0.0.0` and update `WsUrl`.
- **Redundant order broadcast**: the `order` broadcast after matching is noisy
  since clients already receive `fill` messages; consider removing.
