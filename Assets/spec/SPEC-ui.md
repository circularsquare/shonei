# SPEC-ui — Inventory UI Panels

## Overview

The inventory UI uses a **split-panel approach**: a global panel that's always visible, and a separate storage panel that appears when a storage inventory is selected (including liquid storage buildings such as tanks). Market inventories temporarily overwrite the global panel (to be reworked later).

```
Canvas
├── Global Panel (always visible)
│   ├── Title ("town" or market name)
│   └── ItemDisplay rows (collapsible tree, quantities, targets)
│
└── StoragePanel (visible when storage selected, incl. liquid storage)
    ├── Title (inventory displayName, e.g. "oak drawer")
    ├── Slot view (compact: "acorns: 8/20", "empty: 0/40")
    └── Allow sub-panel
        ├── AllowAll / DenyAll buttons
        └── ItemDisplay rows (collapsible tree, allow/disallow toggles)
```

## Selection Routing

`InventoryController.SelectInventory(inv)` decides what to show:

| `inv` | Global panel shows | StoragePanel |
|-------|-------------------|--------------|
| `null` | Global quantities + targets (title: "town") | Hidden |
| Storage (incl. liquid) | Global quantities + targets (unchanged) | Shown with inv details |
| Market | Market quantities + targets (title: market name) | Hidden |

Triggered by `MouseController` on left-click (fires on `MouseButtonUp`): storage/market tiles call `SelectInventory(tileAt.inv)`, everything else calls `SelectInventory(null)`.

### Multi-selection

Multiple storage inventories can be selected simultaneously. The **primary** inventory (most recently touched) is shown in StoragePanel. All selected inventories are highlighted with pool GOs managed by `InventoryController`.

| Control | Action |
|---------|--------|
| LMB drag | Draw rectangle; select all storage inventories whose tile falls inside |
| Ctrl+LMB | Toggle a single storage in/out of the current selection |
| LMB click (no modifier) | Clear selection, select only the clicked inventory |

**State**: `InventoryController.selectedInventories` (`List<Inventory>`) — the full selection. `selectedInventory` remains the primary. `SelectInventories(invs, primary)` sets both at once (used by drag-rect). `CtrlToggleInventory(inv)` adds/removes one entry.

**Filter operations on multi-selection**:
- **Allow/disallow toggle** (`ItemDisplay.OnClickAllow`): toggles the primary, then fans the resulting absolute state to all others (avoids flip-flop when secondary inventories diverge). Group items recurse via `SetAllowStateRecursive`.
- **AllowAll / DenyAll buttons**: apply to all `selectedInventories`.
- **Copy filters** (Shift+LMB): copies from the single clicked tile only — unaffected by multi-selection.
- **Paste filters** (Shift+RMB): pastes to the single clicked tile only.

**Drag-rect visual**: a screen-space `Image` UI element (`DragRect`) on the Canvas, positioned and sized each frame by `MouseController.UpdateDragRect`. Inactive when not dragging.

**Highlight pool**: `InventoryController._highlightPool` — list of `tileHighlightPrefab` instances grown on demand, never destroyed. `RefreshHighlights()` positions and activates/deactivates them to match `selectedInventories`.

## ItemDisplay (row component)

`Assets/UI/ItemDisplay.cs` — prefab at `Assets/Resources/Prefabs/ItemDisplay.prefab`.

Each row represents one item type in the tree. The same prefab is used in both the global panel and the StoragePanel allow tree, configured via `DisplayMode`:

| DisplayMode | Targets (+/-) | Target text | Allow toggle | Used by |
|-------------|---------------|-------------|--------------|---------|
| `Global` | Visible | Visible | Hidden | Global panel |
| `Storage` | Hidden | Hidden | Visible | StoragePanel allow tree |
| `Market` | Visible | Visible | Hidden | Global panel in market mode |

### Per-panel configuration fields

Set at instantiation time (defaults fall back to InventoryController for backward compat):

- `panelRoot` — `RectTransform` for layout rebuilds on collapse/expand. Default: `InventoryController.inventoryPanel`.
- `targetInventory` — `Inventory` for allow/disallow operations. Default: `InventoryController.selectedInventory`.
- `getDisplayGo` — `Func<int, GameObject>` for looking up sibling ItemDisplays in the tree. Default: `InventoryController.itemDisplayGos`.

### Serialized inspector references

- `itemText` — TMP text for item name + quantity
- `targetText` — TMP text for target display ("/100")
- `toggleGo` — the allow/disallow Toggle GameObject
- `targetUpGo`, `targetDownGo`, `targetTextGo` — target button and text GameObjects (for show/hide)
- `spriteOpen`, `spriteCollapsed`, `spriteLeaf` — dropdown arrow sprites

## Global Panel

Managed by `InventoryController` (`Assets/Controller/InventoryController.cs`).

- ItemDisplay instances are created once in `AddItemDisplay()` during first `TickUpdate`, one per item in `Db.items`.
- Tree structure: root items parent to `inventoryPanel.transform`, children parent to their parent ItemDisplay's transform.
- **Discovery**: items are hidden until `globalInventory.Quantity > 0` (checked recursively via `HaveAnyOfChildren`). Once discovered, stays visible even if quantity drops to 0.
- **Tree collapse**: `IsVisibleInTree` walks ancestors — if any parent ItemDisplay has `open == false`, the item is hidden.
- **Targets**: stored in `InventoryController.targets[itemId]` (default 10000 fen = 100 liang). Adjusted via +/- buttons (doubles/halves). Used by `Recipe.Score()` for work order prioritization.
- **Market mode**: when a market is selected, the global panel temporarily shows market quantities and market-specific targets (`inv.targets[item]`) instead of global ones.

## StoragePanel

`Assets/UI/StoragePanel.cs` — singleton MonoBehaviour, starts inactive.

### Slot view (compact)

Shows one row per physical `ItemStack` in the inventory via `StorageSlotDisplay` (`Assets/Components/StorageSlotDisplay.cs`):
```
acorns: 8/20
sawdust: 9/20
empty: 0/40
```

Populated from `inv.itemStacks`. Refreshed every tick via `UpdateSlots()`.

### Allow sub-panel (tree)

A second set of ItemDisplay instances (separate from the global panel's) with `DisplayMode.Storage`. Shows the full item hierarchy with allow/disallow toggles. Only discovered items are visible.

- `allowDisplayGos` — private `Dictionary<int, GameObject>` keyed by item id (independent of global panel's `itemDisplayGos`)
- Items incompatible with the inventory type are filtered out (`Inventory.ItemTypeCompatible`)
- `item` field is set directly at instantiation (bypassing `Start()` timing) so toggles display correctly on the first frame

### Lifecycle

- `Show(inv)` — activate, populate slots + allow tree, force layout rebuild
- `Hide()` — deactivate children (`SetActive(false)` before `Destroy` to avoid layout glitches from deferred destruction), clear dictionaries
- `UpdateDisplay()` — called from `InventoryController.TickUpdate()` when panel is active; refreshes slot text and toggle states

### Allow/Disallow

- Toggle click → `ItemDisplay.OnClickAllow()` → `inv.ToggleAllowItem()` or `ToggleAllowItemWithChildren()` (for parent groups) on primary, then fans absolute state to all `selectedInventories`
- Disallowing an item with existing quantity triggers `RegisterStorageEvictionHaul` (p3 haul work order)
- Auto-allow parent: if all discovered siblings become allowed, the parent group is auto-enabled
- AllowAll / DenyAll buttons wired to `StoragePanel.OnClickAllowAll/DenyAll` — apply to all `selectedInventories`
- Copy/paste filters: Shift+LMB on storage = copy, Shift+RMB = paste (handled by `MouseController` → `InventoryController.CopyAllowed/PasteAllowed`); operates on the single clicked tile regardless of multi-selection

## InfoPanel (tabbed)

`Assets/UI/InfoPanel.cs` — singleton tab container that displays details about selected entities.

When the player clicks a tile, `MouseController` builds a `SelectionContext` (tile + structures + blueprints + animals) and calls `InfoPanel.ShowSelection(ctx)`. InfoPanel creates one tab button per entity and delegates rendering to three sub-views:

| Sub-view | File | Shows |
|----------|------|-------|
| `AnimalInfoView` | `Assets/UI/InfoViews/AnimalInfoView.cs` | Single animal stats, task, skill widgets |
| `StructureInfoView` | `Assets/UI/InfoViews/StructureInfoView.cs` | Structure/blueprint info + controls |
| `TileInfoView` | `Assets/UI/InfoViews/TileInfoView.cs` | Tile coords, type, water, floor inventory |

### Tab ordering
Animals first → structures by increasing depth → blueprints by depth → tile last. Tab label uses tile type name (e.g. "dirt") if non-empty, otherwise "tile". First tab is auto-selected.

### AnimalInfoView skill widgets
Skills are shown as `SkillDisplay` prefab instances (`Assets/Components/SkillDisplay.cs`) spawned into a `skillsContainer` layout group. One widget per `Skill` enum value, rebuilt on `Show()`, refreshed on `Refresh()`.

Each widget: icon (hover → skill name tooltip) + "lv{n}" label + progress bar. Bar fill uses anchor-based sizing (`anchorMax.x = xp/threshold`) so the rect actually resizes rather than just clipping rendering. Bar hover tooltip shows exact xp (e.g. "1.0/20").

Sprites loaded from `Resources/Sprites/Skills/{skillname}` with `Sprites/Skills/default` fallback.

### StructureInfoView controls
- **Enable/Disable** toggle (buildings only) — sets `Building.disabled`, which gates all WOM orders via `isActive` callbacks.
- **Priority +/-** (blueprints only) — adjusts `Blueprint.priority`.
- **Worker slots +/-** (multi-slot workstations only) — adjusts `Reservable.effectiveCapacity`. Priority and worker values render on dedicated TMP labels next to their buttons, not in the main text.

### SelectionContext
`Assets/Model/SelectionContext.cs` — plain C# class built by `MouseController.HandleSelectClick`. Contains `tile`, `List<Structure>`, `List<Blueprint>`, `List<Animal>`. Factory: `SelectionContext.FromTile(tile, animals)`.

### Backward compatibility
`ShowInfo(object)` wraps raw args into a `SelectionContext`. `UpdateInfo()` refreshes the active sub-view (called each tick from `World.cs`). `obj` property returns `currentSelection?.tile` for Blueprint.cs checks.

## GlobalHappinessPanel

`Assets/UI/GlobalHappinessPanel.cs` — exclusive panel showing colony-wide happiness.

Opened by clicking the happiness HUD element (`AnimalController.happinessPanel`); that GameObject needs a **Button** component with onClick wired to `GlobalHappinessPanel.instance.Toggle()`.

**Layout** (set up in editor):
- `headerText` TMP — colony average score + pop capacity
- `needContainer` Transform (VerticalLayoutGroup) — `HappinessNeedRow` instances spawned here lazily on first open
- `needRowPrefab` — `HappinessNeedRow` prefab

Rows are spawned lazily on first open (in `OnEnable`) from `Db.happinessNeedsSorted` (one per satisfaction need, plus housing and temperature), so data populates immediately with no 1-frame delay. Adding a new need to JSON auto-adds a row — no code changes needed (but update `Db.happinessNeedsDisplayOrder` for correct ordering). Refreshes every 1 s while open. Closes on click-outside.

### HappinessNeedRow

`Assets/UI/HappinessNeedRow.cs` — one row in the needs table.

Prefab has a HorizontalLayoutGroup with four children: `NeedName` TMP, `Count` TMP (e.g. "4/5"), `FillBar`, `AvgValue` TMP. Three refresh methods:
- `Refresh(satisfied, total, avgVal)` — value-based needs (all satisfaction dictionary entries)
- `RefreshBool(satisfied, total)` — housing (no meaningful avg value)
- `RefreshTemp(avgTempScore)` — temperature (hides the fill bar; only shows score)

### FillBar

`Assets/Components/FillBar.cs` — reusable horizontal fill bar. Single `SetFill(float 0–1)` method, drives `fillImage.fillAmount`. Prefab: root Image (background) + child "Fill" Image (type = Filled, method = Horizontal, origin = Left).

## Exclusive Panels

`TradingPanel`, `RecipePanel`, `ResearchPanel`, and `GlobalHappinessPanel` are mutually exclusive — at most one may be visible at a time. This is enforced via two static helpers on `UI.cs`:

- `UI.RegisterExclusive(gameObject)` — called in each panel's `Awake`/`Start`; adds it to a static registry.
- `UI.OpenExclusive(gameObject)` — closes all other registered panels, then activates this one.

Each panel's `Toggle()` calls `UI.OpenExclusive(gameObject)` when opening, and `SetActive(false)` when closing.

**To add a new exclusive panel**: call `UI.RegisterExclusive(gameObject)` in `Awake`/`Start`, and replace any `SetActive(true)` in the toggle path with `UI.OpenExclusive(gameObject)`.

## Key Files

| File | Role |
|------|------|
| `Assets/Controller/InventoryController.cs` | Global panel, selection routing, discovery, targets |
| `Assets/UI/ItemDisplay.cs` | Row prefab component (tree collapse, targets, allow toggle) |
| `Assets/UI/StoragePanel.cs` | Storage detail panel (slot view + allow tree; handles liquid storage too) |
| `Assets/Components/StorageSlotDisplay.cs` | Compact slot row text display |
| `Assets/Model/Inventory.cs` | Data model (itemStacks, allowed dict, InvType) |
| `Assets/Model/GlobalInventory.cs` | Global quantity totals |
| `Assets/UI/InfoPanel.cs` | Tabbed info panel container (selection → tabs → sub-views) |
| `Assets/UI/InfoViews/StructureInfoView.cs` | Structure/blueprint info + enable/disable, priority, worker controls |
| `Assets/UI/InfoViews/AnimalInfoView.cs` | Single animal info display (spawns SkillDisplay widgets) |
| `Assets/Components/SkillDisplay.cs` | Skill icon + level + XP bar widget (anchor-based fill, Tooltippable on icon + bar hitbox) |
| `Assets/Components/FillBar.cs` | Reusable fill bar (0–1 fraction → fillAmount); used by HappinessNeedRow |
| `Assets/UI/GlobalHappinessPanel.cs` | Exclusive panel: colony happiness overview + per-need breakdown |
| `Assets/UI/HappinessNeedRow.cs` | One need row: name, count, fill bar, avg value |
| `Assets/UI/InfoViews/TileInfoView.cs` | Tile-only info (coords, water, floor inv) |
| `Assets/Model/SelectionContext.cs` | Structured selection data (tile + structures + blueprints + animals) |
