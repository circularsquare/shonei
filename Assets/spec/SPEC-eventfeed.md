# Shonei — EventFeed

In-game alert/message dispatcher. A central pipe for "things the player should know about" — research forgotten, market errors, trade fills, chat, future maintenance breakdowns, etc. Decoupled from any specific UI; subscribers render it however they like.

## Why it exists

Historically the makeshift notification surface was `TradingPanel.AddChat()`. That tied every alert-worthy event in the game to the trading UI. EventFeed extracts the dispatcher so any system can post alerts without importing TradingPanel, and any future UI (toast, sidebar, HUD overlay) can render them without reaching into the trading code.

## API

```csharp
EventFeed.instance.Post(string text, Category category = Category.Alert);
EventFeed.instance.Clear();                 // called from WorldController.ClearWorld
EventFeed.instance.OnEntry;                 // event Action<Entry> — renderers subscribe
EventFeed.instance.history;                 // IReadOnlyList<Entry>, capped at 200
```

`Entry` carries `text` (rich-text allowed), `category`, and `gameTime` (the `Time.time` at post — pauses with `timeScale`).

## Categories

`Alert` / `Info` / `Chat` / `Fill`. Category drives routing (which renderer picks up the entry) — **not** styling. Senders embed `<color=...>` tags inline for color (red for errors, green for success/fills, orange for research forgets).

**Routing rule** (mirrored in the renderer filters below):

| Category | Goes to | Used for |
|---|---|---|
| `Alert` | AlertToast (overlay) | World-state events the player should notice immediately while looking away from chat — placement errors, research forgotten. |
| `Info` | ChatLog (HUD chat log) | Chat-input feedback the player is already looking at — `/give` / `/rain` / `/day` / `/wind` command responses (both errors and successes), connection-offline errors. Red `<color=#cc3333>` tags still mark errors visually within the chat. |
| `Chat` | ChatLog (HUD chat log) | Server chat from other players. |
| `Fill` | ChatLog (HUD chat log) | Server trade fills. |

When adding a new post site: ask "is the player looking at chat when this happens?" If yes → `Info`. If no (mid-action in the world) → `Alert`.

## Lifetime

- Scene MonoBehaviour singleton following the project's `public static XYZ instance { get; protected set; }` pattern. Attached to a root GameObject named `EventFeed` in `Main.unity`.
- Persists across `ClearWorld` / save-load (only `_history` is cleared, not the singleton itself). This means static-event subscriptions (e.g. `ResearchSystem.OnTechForgotten += ...`) are safe — they're made once in `Awake` and torn down in `OnDestroy`.
- The instance becomes available during `Awake`. Subscribers should prefer `Start()` over `Awake()` to avoid Awake-order races: if the subscriber Awakes first, the null-guard on `EventFeed.instance` fails silently and the subscription is never made. Both renderers (ChatLog, AlertToast) subscribe in `Start`.

## Bindings

EventFeed owns its bindings inline in `EventFeed.cs` — no separate Bindings class yet. Current bindings:

| Source event | Category | Color |
|---|---|---|
| `ResearchSystem.OnTechForgotten` | `Alert` | `#ffaa55` (orange) |

Split into `EventFeedBindings.cs` once this table grows past ~5 rows or bindings pick up policy (throttling, deduping, filtering).

Call sites that post directly (not via a binding):

- `TradingPanel.OnClickSendChat` — connection-offline error (`Info`)
- `TradingPanel.HandleCommand` / `CmdGive` / `CmdRain` / `CmdDay` / `CmdWind` — `/give`, `/rain`, `/day [n]`, `/wind [v]` command feedback. Errors and successes both `Info` (rendered side-by-side in chat); red color tag distinguishes errors visually.
- `ChatLog.DisplayChat` — server chat from other players (`Chat`)
- `ChatLog.DisplayFill` — server trade fills (`Fill`)
- `BuildPanel.PlaceBlueprint` — blueprint placement rejections (single-tile and two-click bridge). Reason strings come from `StructPlacement.GetPlacementFailReason` / `GetTwoPointFailReason`; wrapped in `<color=#cc3333>` (red) and posted as `Alert`.
- `WorldController.ShowWelcomeGreeting` — one-time "Welcome to Shonei!" on world entry (`Alert`, so it shows via AlertToast without opening anything). Appends "N players online" only when `TradingClient.OnlinePlayerCount > 1`; waits up to 3 s for the count, omits it when solo/offline.

## Renderers

Two scene-resident renderers subscribe to `OnEntry` and split categories per the routing table above — no entry shows up in both, so there's no double-render.

### ChatLog (HUD chat log)

`Assets/UI/ChatLog.cs`, hosted on the **always-active `ChatPanel`** (bottom-left HUD), so chat works the moment the world loads. Subscribes to `OnEntry` in `Start`, unsubscribes in `OnDestroy`. Renders **everything except `Category.Alert`** — i.e. `Info` (command success), `Chat` (server chat from other players), `Fill` (trade fills). The Alert filter lives in `HandleFeedEntry`. Entries go through the private `AddChat(text)` helper, which caps the visible list at 20 rows. No category-based styling — the rich-text tags in `entry.text` carry the color. Rows persist for the session (cleared only with the rest of the HUD), so no history backfill is needed.

ChatLog also **sources** two of those categories: it subscribes to `TradingClient.OnChat` (→ `Chat`) and `OnFill` (→ `Fill`, plus the `trade_fill` SFX). This used to live on TradingPanel — but that panel is authored inactive and only wakes on first open, so chat/fills/feedback were invisible until then. The market *holdings tree* refresh on a fill (`UpdateMarketTree`, panel-only) stays on `TradingPanel.RefreshMarketOnFill`.

Each row carries a `ChatRowFader` (`Assets/Components/ChatRowFader.cs`): the row stays fully opaque for 60s, then fades to transparent over 5s so stale messages stop cluttering the HUD. Rows are never destroyed by the fade — focusing the chat input snaps every row back to full opacity so the player can read the whole backlog; releasing focus resumes the age-based fade. Alpha is recomputed from the row's age each frame (correct even if the panel was closed mid-life) using `Time.unscaledTime`, matching AlertToast.

### AlertToast (transient overlay)

`Assets/UI/AlertToast.cs`. Subscribes in `Start` (not Awake) per the Awake-order guidance above. Renders **`Category.Alert` only** — the eye-catching brief surface for errors and important notifications. Sits above ChatPanel in the bottom-left HUD.

- Max 5 simultaneous rows; oldest evicted when a 6th arrives.
- Per-row lifetime: 8s real time, then 1s fade-out. Uses `Time.unscaledTime` so toasts still fade while the game is paused.
- Dedupes consecutive identical messages by resetting the existing row's timer (prevents spam from rapid invalid clicks).
- Scene placement: `UI/AlertToast` GameObject sits as a sibling of `ChatPanel`, anchored bottom-left, positioned just above ChatPanel's top edge. Owns its own VerticalLayoutGroup; rows are constructed at runtime following `ChatLog.AddChat`'s pattern so both renderers stay visually consistent.
