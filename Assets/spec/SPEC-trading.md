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

**Chat/fill display:** capped at 20 entries; fills shown in green. Requires
`chatList` VerticalLayoutGroup with **Control Child Size Width ON, Height OFF**;
each row uses ContentSizeFitter (vertical = PreferredSize).

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

### Travel mechanic

Merchants walk to the market tile at x=0 (the "portal"), then disappear for a
transit period representing the journey to/from the distant city.

- `TravelingObjective(durationTicks)` hides the animal (`go.SetActive(false)`)
  and sets `AnimalState.Traveling`. `AnimalStateManager.HandleTraveling()` ticks
  `workProgress` until the duration is reached, then reappears the animal.
- Transit duration: `World.ticksInDay / 8` per leg, same both ways (`Task.MarketTransitTicks`).
- `Nav.FindMarketPath()` uses r=120 (market at x=0 is outside the default r=40
  search range).
- Save/load: traveling state is persisted (`isTraveling`, `travelProgress`,
  `travelDuration` on `AnimalSaveData`). On load, a `ResumeTravelTask` finishes
  the remaining travel ticks before the animal goes idle.

### Player name

`TradingClient.playerName` — hardcoded `"anita"` for now. Used to identify
which side of a fill belongs to this player.

### Merchant job

Dedicated `"merchant"` job in jobsDb. Merchant mice only perform `HaulToMarketTask` and `HaulFromMarketTask`; they do not take craft/harvest jobs. They are the only mice allowed to path to the market building.

Both tasks are registered with `WorkOrderManager` at priority 3 (same tier as general floor hauls). `UpdateMarketOrders(marketInv)` adds a `HaulToMarket` order when the market has any item below target, and a `HaulFromMarket` order when any item is above target. Orders are deduped by market inventory reference. Registration happens in `Reconcile()` (every 10 s), on inventory changes, and after each task finishes.

**Dispatch gate — target edit delay:** `HaulToMarket` orders are suppressed for 30 s after the player manually edits a market target (`Inventory.lastTargetManualUpdateTimer`). This lets multiple target adjustments settle before merchants are dispatched. `HaulFromMarket` is unaffected.

**Task initialization gates (both market tasks):**
- Eep < 80 %: merchant refuses the trip (`animal.eeping.Eepy()`).
- Food provisioning: `PrependFoodFetchForMarketJourney(transitTicks)` estimates food needed as `hungerRate × 2 × (WalkToPortalSeconds=20 + transitTicks)` + `maxFood × hungryThreshold` buffer (so the merchant arrives above the efficiency-drop threshold). It checks body food + food slot, then fetches from the nearest available food source into the food slot if the deficit can be covered. Returns false (aborts Initialize) if it cannot provision enough food. `HaulToMarket` passes `MarketTransitTicks`; `HaulFromMarket` passes `2 × MarketTransitTicks` (two legs).
- Minimum haul quantity: `MinMarketHaulQuantity = 100 fen` (1.0 liang), no exceptions.

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
