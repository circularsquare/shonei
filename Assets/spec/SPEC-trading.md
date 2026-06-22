# Shonei — Multiplayer Trading

Player-to-player marketplace using a WebSocket connection to a separate Go
server. Players can post buy/sell orders, view live order books, execute trades,
and chat with other settlements.

## Architecture

```
Unity Client (TradingClient.cs)
        │  WebSocket  wss://market.anita.garden/ws?token=<authToken>  (ws://127.0.0.1:8083 in dev)
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
- Identity comes from an auth token on connect (`?token=…`); the server resolves
  it to the account username and stamps that onto outgoing chat/order messages.
  See "Accounts & login" below. (Dev/local insecure mode still accepts `?name=`.)
- Unity client auto-reconnects every 20 s when disconnected.
- Server binds to `127.0.0.1:8083` (localhost only for now). Port 8082 is reserved for the MCP for Unity bridge, which Unity launches automatically.

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
| `price_history_query` | Request downsampled price history for an item | `item`, `rangeSec`, `bucketSec` |
| `chat` | Send a chat message to all players | `text` |

`from` is always injected server-side from the connection name.

### Server → Client

| Type | Sent to | Purpose | Key fields |
|---|---|---|---|
| `market_response` | Requester only | Order book snapshot | `item`, `buys[]`, `sells[]` |
| `fill` | All clients | Trade executed | `buyer`, `seller`, `item`, `price`, `quantity` |
| `order` | All clients | Order placement broadcast | `from`, `item`, `side`, `price`, `quantity` |
| `chat` | All clients | Chat message | `from`, `text` |
| `price_history_response` | Requester only | Downsampled price history | `item`, `rangeSec`, `bucketSec`, `startSec`, `endSec`, `samples[]` |
| `online_count` | All clients | Connected-player count; pushed on every connect/disconnect. Bots trade direct against the exchange (no socket), so they're not counted. | `count` |

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

## Price History & Graph

The server logs a bid/ask snapshot of every order book on a fixed interval so
clients can plot price over time in the trading panel.

### Server-side logging (`pricelog.go`)

- `startPriceLogging` runs a ticker goroutine (modeled on
  `DynamicTrader.startFarming`). It snapshots once immediately on startup, then
  every `PriceLogInterval` — default `1 min`; bump to `5 min` for long test
  sessions. Single named const. After the startup snapshot it sleeps to the
  next `PriceLogInterval` wall-clock boundary before starting the ticker, so
  samples land near :00 of each minute regardless of server start time (and
  stay aligned across restarts).
- Each tick records a `PriceSample{ t, bid, ask }` per item: `t` = unix
  seconds, `bid`/`ask` = best bid/ask price in fen, `0` when no order rests on
  that side.
- History is an in-memory per-item ring buffer capped at `PriceLogMaxSamples`
  (10080 — 7 days at 1/min, so the week view has data). One minutely stream;
  the coarser views are downsampled from it per query, not logged separately.
- **Persisted to disk** (`pricelog.json`, gitignored): the whole store is
  rewritten atomically (temp file + rename) after every tick and reloaded on
  startup, so history survives a server restart. Because samples carry
  wall-clock timestamps, server downtime leaves a real time gap in the data
  rather than corrupting it.
- **Downsampled per query.** `price_history_query {item, rangeSec, bucketSec}` →
  the server takes the window `[now-rangeSec, now]`, buckets samples by
  `floor(t / bucketSec)`, keeps the **first** sample of each non-empty bucket
  (`t` snapped to the bucket start), and replies `price_history_response
  {item, rangeSec, bucketSec, startSec, endSec, samples[]}`. Empty buckets are
  simply absent; `samples` is always a non-nil array. `downsample()` is a pure
  function in `pricelog.go`.

### Client-side graph (`PriceGraph`, `PriceGraphPanel`)

- `PriceGraph` (`Assets/Components/`) draws up to three polylines into a
  `Texture2D` shown through a `RawImage`: the **mid** price (always shown) plus
  optional **bid** / **ask** lines. (A custom `Graphic` / `VertexHelper` mesh
  was tried first but would not render as a runtime-added component; the
  texture path is robust and suits the pixel-art look — point filtering keeps
  it crisp.)
- **Mid-price rule** (`PriceGraph.Mid` — the single tunable spot): both sides →
  `(bid+ask)/2`; bid only → `2×bid`; ask only → `½ ask`; neither → no point.
- **Time X axis + range views.** Three range buttons select the window —
  Hour (3600-s span / 60-s buckets), Day (86400 / 600), Week (604800 / 3600).
  The X axis is real time over `[startSec, endSec]`; each sample sits at its
  timestamp. Two samples connect only when within ~1.5 buckets — a wider gap
  (server downtime, a missing bucket) renders blank. The final segment into the
  live tip is exempt from the gap test (it is continuous with "now"). A side
  with no order also breaks that series' line. The Y axis starts at 0.
- **Axes.** Thin L-shaped X/Y axis lines are drawn along the plot's bottom and
  left edges (in the texture); the plot data is inset to sit inside them. Three
  X-axis labels (scene `TextMeshProUGUI` objects driven by `PriceGraphPanel`)
  show relative times — full span back / halfway / `now`.
- `PriceGraphPanel` (`Assets/UI/`) is a thin controller on the scene-placed
  `PriceGraphContainer` (inspector ref `priceGraphPanel` on `TradingPanel`). Its
  UI — wood-frame background, the graph, the Bid/Ask series toggles, the
  Hour/Day/Week range buttons, the corner labels — is authored as scene
  GameObjects wired into the controller via `[SerializeField]`. It owns the
  selected range; `TradingPanel` reads `RangeSec`/`BucketSec` when it queries.
- `TradingPanel` queries history at the current range in `OnClickQuery` and on a
  `PriceHistoryPollSeconds` (25 s) poll; a range-button click calls
  `RequeryHistory()` for an immediate refresh. Responses for a no-longer-viewed
  item *or* a no-longer-selected range are ignored.

## Unity Client — TradingClient.cs

Singleton (`TradingClient.instance`). Attach to a persistent GameObject.

**Events:**

| Event | Fired when |
|---|---|
| `OnConnectionChanged` | WebSocket connect / disconnect |
| `OnMarketResponse` | Server sends order book snapshot |
| `OnFill` | A trade executes (broadcast to all) |
| `OnChat` | Chat message received |
| `OnOnlineCount` | Player count received (`OnlinePlayerCount` / `hasOnlineCount` cache it; both reset on disconnect) |

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
| `chatInput` | TMP_InputField | Chat message input (the log itself lives on `ChatLog` — see below) |
| `onlineIndicator` | GameObject | Image + TMP child showing online/offline |
| `itemIconGrid` | Transform | GridLayoutGroup container; populated once in `Start()` with one `ItemIcon` per leaf item in `Db.itemsFlat`. Clicking an icon sets `itemInput.text` and triggers `OnClickQuery()`. Groups are skipped — trading is leaf-only. |
| `itemIconPrefab` | GameObject | `Resources/Prefabs/ItemIcon.prefab`. |

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

**Global `/` shortcut:** pressing `/` anywhere outside a text field focuses `chatInput` and seeds it with `"/"`. The trading panel does NOT need to open — `chatInput` lives in the always-active `ChatPanel` (`UI/ChatPanel/ChatBar/ChatInput`) and is wired into `TradingPanel` via the inspector. The seed is set next frame (via a one-frame coroutine running on `chatInput`, not on `TradingPanel` since that MonoBehaviour is inactive when closed) so we overwrite whatever Unity's input system did with the same keystroke — result is always exactly one `/` regardless of selection-timing quirks. Wired in `UI.Update()` → `TradingPanel.OpenChatInput()`.

**Console commands** (parsed locally in `TradingPanel.HandleCommand`, never sent to server):
- `/give [item] [qty in liang]` — produce items into the market inventory.
- `/rain` — toggle precipitation.
- `/day [n]` — jump world clock to day `n` of the current year.
- `/wind [v]` — snap wind to value `v` (positive = right). Both `wind` and `targetWind` are set so the OU walk doesn't immediately pull it back.
- `/timespeed [n]` — set `Time.timeScale` to any value 0–100 (cheat fast-forward beyond the 0x/1x/2x buttons), via `TimeController.SetSpeed`.
- `/mice [n]` — set the colony population to exactly `n`. Above current pop, spawns newcomers clustered on the mouse nearest the original spawn point (`AnimalController.DebugSpawnMice`); below it, randomly culls (`DebugRemoveMice`).
- `/research [id]` — fully research the tech with that `id` (and its prereqs), via `ResearchSystem.MaxTech`. With no `id`, researches everything (`UnlockAll`). Replaces the old debug-mode "unlock all" button.

**Chat/fill display:** the chat log lives on `ChatLog` (on the always-active `ChatPanel`), **not** this panel — so it renders even when the trading panel is closed. `ChatLog` both *sources* server chat + fills into `EventFeed` and *renders* every non-Alert entry as a chat row (capped at 20). TradingPanel keeps only `chatInput` (command entry) and `RefreshMarketOnFill` (refreshes the holdings tree on a fill while open). Market errors, `/give` feedback, server chat, and fills all flow through `EventFeed.Post(...)`; inline `<color=...>` tags drive per-message coloring. See SPEC-eventfeed for the dispatcher contract and ChatLog details.

**Indicator sprites:** loaded from `Resources/Sprites/Misc/indicator/green` and
`Resources/Sprites/Misc/indicator/red`.

## Running the Server

```bash
cd ~/projects/shonei-server
go mod tidy      # first run only
go run main.go   # listens on 127.0.0.1:8083
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
(market → town). The inventory transfer happens at the market, between the two
legs — `HaulToMarket` delivers there; `HaulFromMarket` receives there, then
walks to home storage after the return leg.

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
  on `HaulToMarketTask` / `HaulFromMarketTask`. These rebuild the tail of
  objectives: the unfinished `TravelingObjective` at the canonical full
  `MarketTransitTicks` duration, then deliver/receive + return-leg objectives
  as appropriate. They skip the eep/food gates and the outbound pathing that
  already happened pre-save, and re-issue the market/storage space
  reservations. Reservations themselves are never persisted, so every task
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

### Accounts & login

Players have real accounts (username + password). The Go server (`auth.go`)
exposes `POST /register` + `/login` (JSON) returning an HMAC-signed token; `/ws`
authenticates `?token=` → username. Secret in `/opt/shonei/shonei.env` (secure
mode = token required); locally with no secret it runs insecure and accepts
`?name=` for the CLI test client. NPC trader names are reserved at registration.
Tokens are **stateless** (30-day expiry) and **cannot be revoked before expiry** —
logout only clears the client's cached token; rotating `SHONEI_SECRET` is the only
revocation lever (invalidates *all* tokens at once). Fine for trusted friends; the
plan notes the per-user token-version upgrade if it ever matters.

Client: `Session` (static) holds the logged-in username + token across the
Menu→Main scene load; `MarketServer` resolves the host (prod vs editor-local
toggle); `AuthClient` does register/login; `MenuController`/`Menu.unity` is the
login front-end. `TradingClient.playerName` is now `Session.Username` (with an
editor-only dev fallback when running Main standalone) — used to identify which
side of a fill is this player. Full roadmap: `plans/account-system.md`.

**Sliding renewal + expiry.** `POST /refresh` (bearer auth) trades a still-valid
token for a fresh full-TTL one; the menu calls it silently at startup
(`MenuController.SilentRefresh`), so a player active at least once per TTL never
re-types a password. Refresh failure is a deliberate no-op (the old token keeps
working) — only a real 401 from the save/WS paths kills a session. On a mid-game
401 `SaveSync` calls `Session.ExpireToken()` (drops the token, KEEPS the username
so `StorageScope` doesn't flip to `.guest` mid-session) and raises `OnAuthExpired`
→ in-game `EventFeed` alert (via `SaveSystem`), menu bounce-to-login
(`MenuController`). The login form pre-fills the last username; an expired
remembered token also lands there via `Session.LoadRemembered`'s validity check.

### Account-owned cloud saves

Saves belong to the logged-in account, mirrored to the server. **The local file
stays authoritative** — `SaveSystem.Save` writes synchronously as before, then (if
`Session.LoggedIn`) hands the already-serialized JSON to `SaveSync.QueueUpload` for
an async, best-effort background upload. The network is never on the save's critical
path; offline just means a stale mirror.

Server (`saves.go`, token-authed HTTP, decoupled from the trading WS): `GET /saves`
(metadata only, from `<slot>.meta.json` sidecars), `GET/PUT/DELETE /save?slot=`. Blobs
are stored **gzipped + opaque** (server never decompresses; client gzips/gunzips —
served as `application/octet-stream`, NOT `Content-Encoding`, since UnityWebRequest
can't be relied on to auto-inflate). Auth is `Authorization: Bearer <token>` reusing
`verifyToken`. Per-account quota + server-side autosave rotation (`MaxCloudAutosaves`)
— the latter because per-machine-timestamped autosave names mean client rotation can't
bound cross-device growth. `DELETE` writes a **tombstone** (rev-bumped meta, blob
removed) so a stale offline device can't resurrect a deleted slot.

Client: `SaveSync` (static: upload pump + list/download/delete coroutines) runs its
pump on `SaveSyncRunner` (DontDestroyOnLoad — must outlive the Menu↔Main load, since a
final save fires as the player returns to the menu and `SaveSystem` is Main-only).
`SaveSyncIndex` holds per-slot markers `{uploadedAt, cloudRev}` + a stable machine GUID,
stored OUTSIDE `SaveDir` (`SaveStore` filters dot-prefixed files so it's never a phantom
slot). **Local saves are themselves per-account** (`SaveStore.SaveDir` = `<root>/<account>`,
`account` = `Session.StorageScope`), so the sync markers are keyed per account too
(`SaveSyncIndex.ScopedKey` = `<account>/<slot>`) — two accounts on one machine keep
independent local slot sets *and* sync lineages. Conflict status is **rev-based** (server-assigned `rev`, not wall clocks);
`savedAt` is display/tiebreak only. `MenuLoadPanel` async-merges local + cloud into
one badged row per slot (`SaveSync.SyncStatus`: synced/local/cloud/cloud-newer/conflict/
update-needed); cloud picks download to disk then boot the normal load path. Continue is
cloud-aware. A conflict row prompts three-way **cloud / local / both**
(`MenuLoadPanel.KeepBoth`): "both" sets the local divergence aside as
`"<slot> local <date>"` (rename, sync marker removed — independent lineage) and adopts
the cloud copy under the original name, so the cloud rev lineage stays put and nothing
is lost; the list refreshes instead of booting. The third button is
`ConfirmationPopup.altButton` — assigned only in Menu.unity (Main's popup stays
two-button; `Show` degrades gracefully when alt is unassigned). A save whose `saveVersion` exceeds this build is refused on download. The
**in-game** `SaveMenuPanel` shows a simpler **network-free** per-row badge
(`SaveSync.LocalBadge`: synced/syncing/local/offline) from the local marker only —
cross-device status is a menu concern — refreshed live via `OnStateChanged`.

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

**Graceful at-market fail (`Task.FailAtMarket`).** The merchant's sprite is parked at the town portal for the entire market phase — on a plain `Fail()` they would snap idle at x=0 as if they never travelled. Instead, at-market objectives (currently `ReceiveFromInventoryObjective`) call `task.FailAtMarket()` on any abort path. `HaulFromMarketTask` and `HaulToMarketTask` override it to clear the remaining queue, enqueue a single return `TravelingObjective(MarketTransitTicks)`, and `StartNextObjective()`. The task stays alive so reservations unwind in the normal `Cleanup` once travel completes. `iq` / `storageTile` / `pickupIq` are nulled in the override so a mid-return save emits no task descriptor — the loader falls through to `ResumeTravelTask` and finishes the tail rather than trying to deliver items the merchant never picked up.

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
buyPrice  = sellPrice / 4
```
`buyPrice` is what the nation pays the player when buying the player's exports —
the `/4` spread means a refined good's `defaultPrice` must clear ~4× the player's
input cost before exporting it turns a profit. (See `shonei-server/CLAUDE.md`,
which is authoritative for server mechanics.)

**Stock is the single source of truth** (`bots.go`). Price is a pure function of
real `stock`; nothing sets price (or stock) directly. Each farming tick (10 s)
stock is shifted by two additive forces, plus fills — so the quote only moves
because supply moved, and never jumps discontinuously:
- **Mean-reversion speed (`FarmingRateScale`, all traders):** only this fraction
  (0.125) of the full `stockDelta` is applied each tick; the sub-fen remainder is
  carried in `farmAccum` so the slow pull isn't lost to integer rounding (which
  would leave a dead zone near target). At 0.125 a +100% supply shock decays back
  to ~+10% over **~2 h** of real time for a typical trader (was ~15 min at 1.0).
  One knob for reversion speed; `traders.json` gain/loss stay as relative rates.
- **Seasonal swing (crops only):** shifts the *farming target* stock reverts
  toward (`currentTarget = defaultStock × (1 + offset)`). The offset is an
  asymmetric sawtooth — a linear ramp-up from −20% to +40% across a quarter-year
  harvest window (stock accumulates → cheaper), then a slow linear decline back
  to −20% over the rest of the year. Period = one player year at 1×
  (`SeasonalYearSeconds = ticksInDay 480 × daysInYear 24 = 11520 s`); **only the
  frequency is shared with player time** — the phase is wall-clock-anchored,
  deliberately *not* aligned to any player's in-game season. Per-crop
  `harvest_phase` (in `traders.json`) staggers crops so they don't all glut at once.
  Note: because the seasonal swing is tracked through the (now slow) farming pull,
  realized stock **lags and undershoots** the target swing — e.g. a grain trader
  realizes ~+30%/−3% against the +40%/−20% target (`TestSeasonalSwingDamping`).
- **Noise (all traders):** a small random supply shock added to stock each tick
  (`N(0, NoiseKickFactor × maxStockGain)`, ~1% of stock per tick). Farming's
  mean-reversion accumulates it into a *stationary* spread of ~10% of the
  no-noise stock — an Ornstein-Uhlenbeck process (same idea as the wind OU). The
  kick→spread amplification depends on the reversion speed, so `NoiseKickFactor`
  is **tuned by simulation** (`TestNoiseStationarySpread`), not derived — retune
  there if the farming rate **or** `FarmingRateScale` changes.
- A bot re-quotes every tick (stock moves every tick now). Bot re-quotes don't
  broadcast (only fills do), so this adds no client traffic.

**Stock dynamics:** `startFarming()` ticks every 10 s, shifting stock toward the
(seasonal) target. Low stock → gains; excess stock → losses. Max gain/loss per
tick configurable per trader. The same farming pull is what damps the noise into
its ~10% stationary spread, so changing the gain/loss rates also changes the
noise spread (retune `NoiseKickFactor`).

**Stock persistence:** because price is derived purely from stock, the server
persists every trader's current `stock` to `traderstock.json` (gitignored) on
the price-logging cadence (1 min — `saveTraderStock` rides the same ticker as
`logSnapshot`). On startup `initDynamicTraders` loads it: a saved `stock` for a
matching `name`+`item` overrides the `traders.json` value, so a restart resumes
prices where they left off. `traders.json` `stock` is only the cold-start
baseline (no save file, parse error, or a trader newly added to config). Only
`stock` is restored — all pricing config stays authored in `traders.json`.

**Order refresh:** `refreshOrders()` calls `cancelOrdersForItem(name, item)` (scoped
to this trader's item only — important when a nation has multiple traders) then
re-places buy/sell orders at current prices. Called on fill and on each farming tick.

**Config:** `traders.json` — one entry per `DynamicTrader`, and the authoritative
roster. Each entry is fully independent: its own `stock`, price curve, and order
book entry. Traders never share stock — a fill is matched to a trader by
`name`+`item` together, so the `name` ("nation") is essentially cosmetic. It only
drives the `From` label on the order/fill and which trader `getTraderStock(name)`
returns (the first match for that name). Grouping items under a nation is for
player legibility, not mechanics. Item names must be **leaf** items (groups like
`wood`/`tools` are never physical and can't be hauled).

Current roster (one nation per theme): **fulan** — grains/fibre/textile;
**nachria** — food & drink; **trapzon** — ore/metal/stone/glass; **corcyros** —
stone tools; **lakta** — wood goods.

**To add a new nation:** add an entry to `traders.json`.  
**To add more items for a nation:** add another entry with the same `name`.  
**Prices/quantities:** always in fen (100 fen = 1 liang).
