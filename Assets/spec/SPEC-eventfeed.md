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

`Alert` / `Info` / `Chat` / `Fill`. Currently used as metadata only — renderers don't map category → color because senders already embed `<color=...>` tags inline (red for errors, green for success/fills, orange for research forgets). Category becomes useful once we want per-feed filtering, icons, or an overlay that only shows `Alert`.

## Lifetime

- Scene MonoBehaviour singleton following the project's `public static XYZ instance { get; protected set; }` pattern. Attached to a root GameObject named `EventFeed` in `Main.unity`.
- Persists across `ClearWorld` / save-load (only `_history` is cleared, not the singleton itself). This means static-event subscriptions (e.g. `ResearchSystem.OnTechForgotten += ...`) are safe — they're made once in `Awake` and torn down in `OnDestroy`.
- The Instance becomes available during `Awake`. Subscribers should prefer `Start()` over `Awake()` to avoid Awake-order races — the null-guard on `EventFeed.instance` fails silently if the subscriber Awakes first, and the subscription is never made. TradingPanel currently subscribes in `Awake` because EventFeed reliably Awakes first in Main.unity, but new renderers should wire up in `Start`.

## Bindings

EventFeed owns its bindings inline in `EventFeed.cs` — no separate Bindings class yet. Current bindings:

| Source event | Category | Color |
|---|---|---|
| `ResearchSystem.OnTechForgotten` | `Alert` | `#ffaa55` (orange) |

Split into `EventFeedBindings.cs` once this table grows past ~5 rows or bindings pick up policy (throttling, deduping, filtering).

Call sites that post directly (not via a binding):

- `TradingPanel.OnClickSendChat` — connection-offline error
- `TradingPanel.HandleCommand` / `CmdGive` — `/give` command feedback (usage error, unknown item, no market, success, market-full)
- `TradingPanel.DisplayChat` — server chat from other players (`Category.Chat`)
- `TradingPanel.DisplayFill` — server trade fills (`Category.Fill`)

## Renderer — TradingPanel chat list

TradingPanel subscribes to `OnEntry` in `Awake`, unsubscribes in `OnDestroy`. Entries are rendered by the existing private `AddChat(text)` helper, which caps the visible list at 20 rows. No category-based styling — the rich-text tags in `entry.text` carry the color. The chatList rows persist for the lifetime of the panel (it only `SetActive(false)`s on close), so no history backfill is needed on re-open.
