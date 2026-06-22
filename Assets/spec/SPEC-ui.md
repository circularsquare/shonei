# SPEC-ui — Inventory UI Panels

## Overview

The inventory UI uses a **split-panel approach**: a global panel that's always visible, and a separate storage panel that appears when a storage inventory is selected (including liquid storage buildings such as tanks). Market inventory display lives in TradingPanel (right side, independent ItemDisplay tree).

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
| Market / other | Global quantities + targets (unchanged) | Hidden |

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
| `Global` | **Hidden** | **Hidden** | Hidden | Global panel — shows icon + name + count only |
| `Storage` | Hidden | Hidden | Visible | StoragePanel allow tree |
| `Market` | Leaf rows only | Leaf rows only | Hidden | TradingPanel market inventory tree |

**Target editing moved off the always-visible panel.** As of the GlobalInventoryPanel feature, the
always-visible Global panel is read-only (count only) — the editable target chrome (slash + input +
steppers) is **Market-mode only** in `ItemDisplay.SetDisplayMode` (`showLeafTarget = mode == Market`).
Targets are now edited in the full-screen **GlobalInventoryPanel** (below), which writes the same
`InventoryController.targets` dict — single source of truth, unchanged save format.

Group items hold no target (a group input resolves to a concrete leaf at scoring/consumption time
— see SPEC-ai § recipe scoring), so the target widget is hidden on **group** rows in both Global and
Market modes; only leaf rows show it. The +/- handlers also early-return on group items defensively.

**Ctrl-click = 10× on any +/- stepper.** Target steppers (ItemDisplay, InventoryDetailRow) and the
jobs panel +/- buttons multiply their normal step by `UIInput.StepMultiplier` (10 when Ctrl is held,
else 1), read per-click. New +/- buttons should use the same helper for consistency — except small
fixed-range controls (blueprint priority, worker slots) where 10× would just clamp.

**Group rows still show their summed quantity.** The `Quantity` text lives inside the `TargetGroup`
container (alongside `Slash` + `TargetInput`). `SetDisplayMode` keeps `TargetGroup` (`targetTextGo`)
visible in Global/Market for both leaves and groups, and hides only the leaf-target bits
(`slashGo` + `targetInput` + steppers) on group rows — so a group reads as a bare count (e.g. "200")
with no dangling "/target". `GlobalInventory.Quantity` sums leaf descendants for the group total.

### Per-panel configuration fields

Set at instantiation time (defaults fall back to InventoryController for backward compat):

- `panelRoot` — `RectTransform` for layout rebuilds on collapse/expand. Default: `InventoryController.inventoryPanel`.
- `targetInventory` — `Inventory` for allow/disallow operations. Default: `InventoryController.selectedInventory`.
- `getDisplayGo` — `Func<int, GameObject>` for looking up sibling ItemDisplays in the tree. Default: `InventoryController.itemDisplayGos`.

### Refresh contract

`ItemDisplay.Refresh()` is the **single per-mode content repaint** (Global: name + global qty + leaf-only target; Market: name + market qty + leaf-only target; Storage: allow-toggle sprite). The three tree owners (InventoryController, StoragePanel, TradingPanel) keep ownership of row *visibility* — their discovery / class-compat / collapse rules differ — and call `Refresh()` per row. Don't add caller-side content repaint for a new mode; extend the switch in `Refresh()`.

### Serialized inspector references

- `itemText` — TMP text for the item name (left-aligned)
- `quantityText` — TMP text for the current quantity, rendered immediately left of the slash (empty in Storage mode)
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
  - Groups start collapsed by default; flag `defaultOpen: true` in itemsDb.json to start a group expanded (e.g. `"food"`). Market mode always expands every group regardless of the flag.
  - **Global panel** collapse state persists across saves via `WorldSaveData.inventoryTreeOpen` (stores only deltas vs `defaultOpen`); on load the dict is staged on `InventoryController.pendingGroupOpenOverrides` and consumed by `ItemDisplay.Start`.
  - **StoragePanel** allow tree is built once and reused across all `Show()` calls, so its collapse state persists within a session but isn't saved.
- **Targets**: stored in `InventoryController.targets[itemId]` — **leaf items only** (group items hold no target). Seeded from each item's `DefaultTargetFen` (100 liang default; byproducts lower; books 1 liang). Adjusted via +/- buttons in ±1 liang (100 fen) steps. Read by `Recipe.Score()` (input surplus + output scarcity → work-order/craft prioritization) and `Task.ResolveConsumeLeaf` (which leaf a group input consumes).
- **Market display**: the global panel always shows global quantities. Market inventory has its own tree in TradingPanel (see Overview).

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
- **Built once, reused forever**: `BuildAllowTreeOnce()` instantiates one row per item in `Db.items` on first `Show()` and sets `_allowTreeBuilt = true`. Subsequent shows skip the build and just call `RefreshAllowTreeForInv(inv)` to rebind `targetInventory`, recompute visibility (`compat && discovered && parentOpen` walking via `IsVisibleInAllowTree`), and refresh allow-toggle sprites. This keeps click cost flat (tens of SetActive + LoadAllowed calls) instead of paying hundreds of GameObject instantiations per click.
- **Per-inventory filter at refresh time, not build time**: rows are built for every item regardless of class; `Inventory.ItemTypeCompatible` is applied in `RefreshAllowTreeForInv` so the same cached tree serves Default / Liquid / Book inventories.
- `item` field is set directly at instantiation (bypassing `Start()` timing) so toggles display correctly on the first frame. `display.open` is also preempted to `DefaultOpenForGroup(item)` at build time so first-frame visibility is correct before `Start()` runs.

### Lifecycle

- `Show(inv)` — activate, populate slots, `BuildAllowTreeOnce()` (no-op after first time), `RefreshAllowTreeForInv(inv)`, force layout rebuild.
- `Hide()` — deactivate panel; slot rows are destroyed (their structure varies per inventory), allow tree rows persist as inactive children.
- `UpdateDisplay()` — called from `InventoryController.TickUpdate()` while panel is active; rebuilds slots (cheap; few stacks) and calls `RefreshAllowTreeForInv(currentInv)` so newly-discovered items (research unlocks, first-time production) appear within one tick.
- World reset: `InventoryController.ResetState()` calls `storagePanel.Hide()` so cached rows don't keep stale `Inventory` references between worlds. Cached `Db.Item` references are safe — items are loaded once from JSON at startup and never invalidated.

### Allow/Disallow

- Toggle click → `ItemDisplay.OnClickAllow()` → `inv.ToggleAllowItem()` or `ToggleAllowItemWithChildren()` (for parent groups) on primary, then fans absolute state to all `selectedInventories`
- Disallowing an item with existing quantity triggers `RegisterStorageEvictionHaul` (p3 haul work order)
- Auto-allow parent: if all discovered siblings become allowed, the parent group is auto-enabled
- AllowAll / DenyAll buttons wired to `StoragePanel.OnClickAllowAll/DenyAll` — apply to all `selectedInventories`
- Copy/paste filters: Shift+LMB on storage = copy, Shift+RMB = paste (handled by `MouseController` → `InventoryController.CopyAllowed/PasteAllowed`); operates on the single clicked tile regardless of multi-selection

## GlobalInventoryPanel (detailed inventory)

`Assets/UI/GlobalInventoryPanel.cs` — full-screen **exclusive** panel (registers via
`UI.RegisterExclusive`, opens via `UI.OpenExclusive`, 0.5 s refresh while open), modeled on
`RecipePanel`. Opened by the **"details"** button in the always-visible inventory header
(`UI/InventoryScroll/.../InventoryTitle/DetailsButton`, persistent `onClick → Toggle`). It is the
detailed counterpart to the always-visible panel, hosting the controls that were pulled off it.

Per item it shows: **location breakdown** — Total + Storage + Floor + Carried (= `Animal` + `Equip`
slots) + Market — plus the **editable target** (leaf rows; writes `InventoryController.targets`) and a
**"don't consume"** toggle. The four breakdown buckets need not sum to Total; the remainder is in
building buffers (processor/reservoir/blueprint) and Total is authoritative. Breakdown numbers come
from `InventoryController.QuantityIn(item, params InvType[])`.

**Row tree** — `InventoryDetailRow` (`Assets/UI/InventoryDetailRow.cs`, prefab
`Resources/Prefabs/InventoryDetailRow`). Rows are a **flat sibling list** under the scroll content
(parents precede children, `Db.items` order); depth is a leading **indent spacer**, not nested
LayoutGroups (avoids the pixel-font-blur / layout churn nested groups cause here). Collapse =
visibility: the panel's `IsVisible` walks ancestors' own `open` state (independent of the
always-visible panel's collapse, mirroring `InventoryController.IsVisibleInTree` + `discoveredItems`).
The row prefab was built by **cloning ItemDisplay's** styled widgets (ItemIcon, TMP_InputField target,
+/- steppers, dropdown) so styling matches; the consume toggle uses `check` (consumable) / `redx`
(protected). Group rows show their target as a **read-only number** (the sum of discovered leaf
targets, `InventoryDetailRow.BarTarget`) — input non-interactable, steppers hidden — so it isn't
edited per-group; and read protected only when **every** leaf is protected — one click
protects/clears the whole group via `InventoryController.SetConsumptionDisabled`.

**Distribution bar** (`InventoryBar`, `Assets/UI/InventoryBar.cs`) — the "where" column. A
fixed-width pill (`Sprites/Misc/progressbar` used as a `Mask`) with colored segments clipped to the
pill shape: storage (green) / floor (yellow) / mice (gray) / market (blue) / installed=reservoir fuel
+ building furnishings (brown) / elsewhere=blueprint+processor buffers, in transit (orange). The
breakdown columns show only storage/floor/mice/market as numbers; installed + elsewhere appear on the
bar only. Width modes: **target > owned** → bar spans the target, the shortfall is a dark
dull-red deficit zone; **target ≤ owned** → bar spans owned, target is a dark-red marker line
(fraction = target/scale, so it pins to the right end once target ≥ owned). On **leaf** rows the
marker is **draggable** (a wide transparent hit zone + thin overhanging line): drag left/right and
the target commits **on release** to `fraction × scale` (snapped to 0.1 liang) — dragging past the
right edge multiplies the target. Group markers sit flush (no overhang) and aren't draggable.
Per-segment `Tooltippable` ("6.4 in storage"). Segments are code-built once and reused; visibility is
toggled via `Image.enabled` (a disabled Graphic doesn't raycast) — never `SetActive`/disabling the
Tooltippable, which would trip `OnDisable→Hide`. For a **group** row the bar target sums only
**discovered** leaf targets (`InventoryDetailRow.BarTarget`) so it doesn't deficit against
not-yet-unlocked leaves (oak/maple).

The "don't consume" gameplay mechanic (what protection actually gates) lives in **SPEC-systems.md
§Don't-consume**.

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

A `ComfortBar` (`tempBar`, between the text blob and SkillsContainer) shows the mouse's temperature comfort: green band = the clothing/warmth-widened `comfortTempLow..comfortTempHigh`, marker = ambient temp. The numeric "warmth" readout was dropped — warmth's effect is now visible as the widened green band.

### StructureInfoView controls
- **Enable/Disable** toggle — sets `Building.disabled`. Only shown when `disabled` actually gates behaviour: workstations (craft/research via `isActive`), reservoir buildings (fuel supply via `isActive`, plus `LightSource.Update` skips burn and forces `isLit=false` while disabled — so light goes dark and fuel stops draining), and leisure buildings (animals skip disabled ones in `LeisureTask.Initialize`). Hidden for storage-only, beds, decorative, plants, and base structures.
- **Priority +/-** (blueprints only) — adjusts `Blueprint.priority`.
- **Worker slots +/-** (multi-slot workstations only) — adjusts `Reservable.effectiveCapacity`. Priority and worker values render on dedicated TMP labels next to their buttons, not in the main text.
- **Harvest flag toggle** (plants only) — calls `Plant.SetHarvestFlagged`, which registers or unregisters the harvest WOM order and toggles the overlay sprite. Label flips between "flag for harvest" / "unflag harvest".
- **Comfort bars** (plants only) — temperature + soil-moisture shown as two `ComfortBar` widgets (`TempBar`/`MoistureBar` rows) instead of text. Yellow track = stalled range, green overlay = comfortable band, circle marker = current value; hover shows exact range + value + the growth-effect note. `ShowPlantComfort` drives them per refresh; `SetComfortVisible(false)` hides them for buildings/blueprints/base structures.
- **Target-gated dormancy alert** (plants only) — when a flagged plant is ripe but every product is at/above its global target, the panel shows red "will not harvest: outputs above target". Mirrors the WOM order's `isActive` gate; the order also surfaces with `[inactive]` next to its `wo: Harvest 0/1` row (via `AppendTileOrders`'s shared `[inactive]` suffix, same as `AppendBuildingOrders`).
- **Cost rows** (construction blueprints only) — one `BlueprintCostRow` per `Blueprint.costs[i]`, spawned from `costRowPrefab` into the scene-authored `CostRows` container (a VerticalLayoutGroup at sibling index 1, after `Text`). Each row shows the item icon + `name have/need`. For a group-item cost currently locked to a concrete leaf, an **X** button disallows that variant (`Blueprint.DisallowLeaf`; tooltip "disallow {leaf}"); one cancel chip per already-banned leaf re-allows it (`AllowLeaf`; tooltip "{leaf} disallowed"). Chips are built in code (icon-only). **Lifecycle gotcha:** rows are rebuilt structurally only on tab-show and after a ban toggle (`RebuildCostRows`), never per tick — per-tick `Refresh` calls `BlueprintCostRow.UpdateDynamic` (label + idempotent X visibility) so a hovered tooltip isn't torn down by a SetActive/Destroy (see the Tooltippable per-tick memory). Deconstruct blueprints show no rows. See SPEC-ai §Blueprint costs for the model side.

### SelectionContext
`Assets/Model/SelectionContext.cs` — plain C# class built by `MouseController.HandleSelectClick`. Contains `tile`, `List<Structure>`, `List<Blueprint>`, `List<Animal>`. Factory: `SelectionContext.FromTile(tile, animals)`.

### Backward compatibility
`ShowInfo(object)` wraps raw args into a `SelectionContext`. `UpdateInfo()` refreshes the active sub-view (called each tick from `World.cs`). `obj` property returns `currentSelection?.tile` for Blueprint.cs checks.

## Collapsible panels

Some always-visible panels can be collapsed to a single header row to reclaim
screen real estate. Implemented via `Assets/Components/CollapsibleHeader.cs`
— a reusable MonoBehaviour that sits at **sibling index 0** of a panel's
VerticalLayoutGroup and toggles every later sibling on click.

Current adopters:
- **Inventory panel** (`InventoryController.inventoryHeader`, saveKey `"inventory"`)
- **Jobs panel** (`AnimalController.jobsHeader`, saveKey `"jobs"`)

The "header is sibling 0, content is the rest" pattern means runtime-spawned
rows (ItemDisplay, JobDisplay) need no re-parenting — they continue to spawn
at the end of the panel and become "content" siblings automatically.

**Spawn-site collapse awareness**: when adding a new row to a collapsed panel,
the spawn site (`InventoryController.AddItemDisplay`,
`AnimalController.AddJobRow`) checks the header's `open` state and starts the
new row inactive. Otherwise newly-spawned rows would pop through a collapsed
panel.

**Click handling**: `CollapsibleHeader` implements `IPointerClickHandler` on
the row root. The row has a transparent Image with `raycastTarget=true` so
clicks anywhere on the row (including the gap between arrow and label) fire
the toggle.

**Persistence**: `WorldSaveData.panelsOpen` (`Dictionary<string, bool>`)
stores only deltas vs default-open (true), keyed by `CollapsibleHeader.saveKey`.
Gather/restore live in `SaveSystem.cs` alongside `inventoryTreeOpen`.
`ResetSystemState` sets both headers back to open on a fresh world.

**Adding a new collapsible panel**:
1. Add a header GameObject as sibling 0 of the panel's content container,
   with HLG + transparent raycast Image + DropdownArrow child + TitleLabel
   child + `CollapsibleHeader` component.
2. Pick a unique `saveKey` (matches `Dictionary<string, bool>` keys).
3. Add a `[SerializeField] CollapsibleHeader yourHeader;` ref on the
   controller that owns the panel and wire it in the editor.
4. Update `SaveSystem.GatherSaveData` and the Phase 5 restore block in
   `ApplySaveData` to read/write the header's state via its `saveKey`.
5. Update `SaveSystem.ResetSystemState` to set the header back to open.
6. If the panel spawns rows at runtime, gate their initial `SetActive` on
   `header.open`.

## Avoiding layout pop on reveal (LayoutUtil)

**The bug:** when you `SetActive(true)` a UI subtree (or spawn rows into one), Unity
only schedules a layout rebuild for end-of-frame, so the content shows at
padding-only height for one frame then snaps to full size — a visible "pop". A single
top-down `LayoutRebuilder.ForceRebuildLayoutImmediate` does **not** fix this when the
subtree has **nested** LayoutGroups/ContentSizeFitters (e.g. RecipePanel: content →
group → cards container → card → section → row): each parent fitter measures its
children before they're sized, so it takes multiple frames to settle.

**The fix — always use `LayoutUtil.RebuildImmediate(rect)`** (`Assets/UI/LayoutUtil.cs`)
after toggling visibility or spawning content. It (1) calls
`Canvas.ForceUpdateCanvases()` once so dirtied TMP reports current preferred sizes,
then (2) rebuilds **bottom-up** (children before parents) so nested fitters resolve in
one frame. Pass the **outermost** rect whose size depends on the change (e.g. the
scroll Content), not just the toggled row — its ancestors' fitters must re-measure too.

Adopters (all reveal/resize-after-content-change paths): `CollapsibleHeader`,
`RecipeGroupDisplay`, `RecipePanel`, `ItemDisplay` (tree dropdown), `InventoryController`
(discover/clear reflow), `InfoPanel` (tab spawn), `StoragePanel.Show`, `TradingPanel`
(market tree + chat), `AlertToast`. When building a new expand/collapse or reveal, call
`LayoutUtil.RebuildImmediate` rather than rolling a bespoke `ForceRebuildLayoutImmediate`.

Exception: `TooltipSystem.Show` stays bespoke — it isn't a pop case but a precise
measure-then-position that calls `ForceMeshUpdate` on its two specific TMPs (tighter
than a canvas flush) before measuring; runs on every hover, so left untouched.

## GlobalHappinessPanel

`Assets/UI/GlobalHappinessPanel.cs` — exclusive panel showing colony-wide happiness.

Opened by clicking the top-bar happiness readout — a `Button` on `UI/TopBar` wired **in code** via `AnimalController.happinessButton` → `GlobalHappinessPanel.instance.Toggle()`. `instance` is a lazy getter that resolves the (inactive) panel via `FindObjectOfType(true)`, so the first click works before the panel has ever activated (without it, `Awake` hasn't run and `instance` is null → silent no-op). The readout is the single entry point — the old standalone `HappinessToggle` button was removed.

**Layout** (set up in editor):
- `headerText` TMP — colony population, average happiness, happiness population cap, and available housing (beds). The "grow pop" levers live in the "?" tooltip, not the header.
- `needContainer` Transform (VerticalLayoutGroup) — `HappinessNeedRow` instances spawned here lazily on first open
- `needRowPrefab` — `HappinessNeedRow` prefab

The panel root is a `VerticalLayoutGroup` stacking, top→bottom: `headerText`, the scroll view, a `FoodChartLabel` TMP, and the `FoodChart` (`BarChartGraph`). The header is **content-driven** (`LayoutElement.preferredHeight = -1`, so the VLG reads TMP's height — note it's ~4 lines / 53px at runtime vs the 1-line placeholder in edit mode) and the scroll has `flexibleHeight = 1` to absorb the difference — so adding/removing a header line reflows automatically. Don't pin the header height or hand-position the scroll (that reintroduces overlap). The X + "?" buttons use `LayoutElement.ignoreLayout = true` to stay corner-anchored outside the stack.

**Scroll-content setup (load-bearing):** the scroll content `HappinessList` is **top-stretch anchored** (`anchorMin (0,1)`, `anchorMax (1,1)`, `pivot (0,1)`) with a **`ContentSizeFitter` (vertical = PreferredSize)** so its height tracks the spawned rows. Without these the content height is pinned to the viewport and the `ScrollRect` never sees overflow → rows past the fold are unreachable and drags snap back (the rows just happened to fit while the panel was tall). Any scrollable list needs this pair.

Rows are spawned lazily on first open (in `OnEnable`) from `Db.happinessNeedsSorted` (one per satisfaction need, plus housing, furnishing, temperature, and the colony food-storage row). Adding a new need to JSON auto-adds a row — no code changes needed (but update `Db.happinessNeedsDisplayOrder` for correct ordering). Refreshes every 1 s while open. Closes on click-outside.

### HappinessNeedRow

`Assets/UI/HappinessNeedRow.cs` — one row in the needs table. Single prefab; per-row variation comes from runtime config, **not** prefab variants. Adding new prefabs per row would fragment styling and break the data-driven model (per-need rows come from JSON).

**Contract (every row follows this exactly):**

1. `Configure(name, points)` — called **once** at spawn. Sets the name AND sizes the fill bar to `BarWidthPerPoint × points`. The row stores `maxPoints`; this enforces the bar-width-encodes-point-value invariant structurally (callers cannot pass mismatched fill + width at refresh time).
2. `Refresh(averagePoints, detailText = "")` — called **every tick**. `averagePoints ∈ [0, maxPoints]` is the row's avg happiness contribution; the row displays it as `avg/max` in the middle text (`AvgHappiness` column, prefW 36 to fit `x.x/y.y` in m5x7 @16) and fills the bar to `averagePoints / maxPoints`. `detailText` is an optional raw underlying value (e.g. raw satisfaction avg for the wheat row), shown in the right column **only in Ctrl+D debug mode** — it reads confusing to players, so it's dev-gated. Pass `""` for rows that don't expose one.

`points` per row type: value/bool needs = 1, temperature = 2, furnishing = `Db.maxFurnishingPerMouse`, food storage = `AnimalController.MaxFoodStorageBonus`. The panel's `SpawnRow(key, points)` helper is the single place these values are declared.

**Anti-pattern**: do **not** add a 3rd specialized `RefreshXyz(...)` method when introducing a new row type. The Refresh API is deliberately single-shape — derive `averagePoints` and `detailText` in the panel's switch instead. If the row genuinely needs a new visual element (e.g. an icon), add it to the prefab and a setter on `Configure`, not a new Refresh variant.

### FillBar

`Assets/Components/FillBar.cs` — reusable horizontal fill bar. Single `SetFill(float 0–1)` method, drives `fillImage.fillAmount`. Prefab: root Image (background) + child "Fill" Image (type = Filled, method = Horizontal, origin = Left).

### Food chart

`FoodChart` is a `BarChartGraph` (a `RawImage` below the needs scroll) wired to the panel's `foodChart` field. Each `Refresh` feeds it via `SetSeries`: `food_produced` (up) and a stacked down bar of `food_consumed` (eaten) + `food_decayed` (spoilage, muted Slate). Diverging per-day chart with per-bar hover tooltips that break out each segment. `BarChartGraph` is generic/reusable; the chart/stat machinery lives in **SPEC-stats.md**. (`ResearchPanel` has a parallel `researchChart` — scientist+passive gain up, decay down; see SPEC-research.)

## Top-bar readouts

Two small HUD readouts driven from code (no panel), authored as scene TMPs:

- **Low-food warning** — `AnimalController.foodWarningText`, a red TMP under the happiness
  readout (`UI/TopBar/FoodWarning`). `UpdateColonyStats` → `UpdateFoodWarning` shows it as
  "X.x days of food" only while `daysOfFoodInStorage < FoodWarnDays` (2.5); hidden when food is
  healthy / colony empty.
- **Season/time display** — `SeasonTimeDisplay` (`Assets/Components/SeasonTimeDisplay.cs`), a TMP
  left of the time-speed buttons (`UI/SeasonTimeDisplay`, sibling of `TimeController`, right-aligned
  so it grows leftward). Escalates with progress: season always ("winter"); + day-of-season once the
  **Timekeeping** tech is unlocked ("winter 2"); + hour while any built clock is powered ("winter 2 5pm").
  Reads `WeatherSystem.GetSeason/GetDayOfYear`, `ResearchSystem.IsUnlockedByName`, a powered-clock
  scan (`StructController.GetByType` + `PowerSystem.IsBuildingPowered`), and `SunController.GetDayPhase`.

## Build Bar & Mouse Modes

The build bar (`BuildCategoryBar` in Main.unity) holds both category buttons and standalone mode buttons. Each standalone button calls a `SetMode*()` method on `MouseController`, which sets `MouseController.mouseMode` (enum `MouseMode { Select, Build, Remove, Harvest }`) and clears `BuildPanel.structType`.

**Category buttons are data-driven.** `BuildPanel.Categories` is a `{key, label, icon}` array (Tiles, Structures, Plants, Production, Power, Housing, Storage); at `Start` each entry is spawned from the `CategoryButton` prefab into `BuildCategoryBar` at `categoryInsertIndex` (between the leading/trailing tool buttons), with its icon loaded from `Sprites/Misc/buildicons/<icon>` and onClick/tooltip wired in code. Adding a tab = one `Categories` entry + a `buildicons/<icon>.png` + author the buildings' JSON `category` — **no scene wiring or per-category SerializeField**. Housing buildings (`isHousing`) live in the `housing` category, not `storage`.

| Mode | Trigger | Behavior |
|------|---------|----------|
| `Select` | Default; Escape returns here | LMB = click/drag-select inventories; shows InfoPanel |
| `Build` | Category → structure button | LMB = place blueprint of current `BuildPanel.structType` |
| `Remove` | "Remove" button | LMB undoes one thing per click, in priority order (`BuildPanel.Remove`): cancel a blueprint here (construct or deconstruct) → clear a plant's harvest flag → else mark a structure for deconstruct (background-first via `CreateDeconstructBlueprint`). |
| `Harvest` | "Harvest" button | LMB click or drag calls `Plant.SetHarvestFlagged(true)` on all plants under the cursor/rect, which registers a harvest WOM order for each. Worldgen plants start unflagged; plants finished from a player-placed blueprint come out flagged automatically (via `Plant.OnPlaced`). Paint-only — to *un*flag, use the Remove tool (clears the flag before it would deconstruct the plant). |

Harvest and Select both use the shared `_dragStartScreenPos` / `_isDragging` / `DragThresholdPixels` drag-rect machinery (tracked via a single nullable `_dragStartedInMode` so a mode change mid-drag can't commit into the wrong handler). Screen→world rect math is shared via `GetDragWorldBounds`. The visual indicator for a flagged plant is a child GameObject under `Plant.go` with its own `SpriteRenderer` (sprite at `Resources/Sprites/Misc/harvestselect`), toggled visible by `Plant.SetHarvestFlagged`.

## Exclusive Panels

`TradingPanel`, `RecipePanel`, `ResearchPanel`, and `GlobalHappinessPanel` are mutually exclusive — at most one may be visible at a time. This is enforced via two static helpers on `UI.cs`:

- `UI.RegisterExclusive(gameObject)` — called in each panel's `Awake`/`Start`; adds it to a static registry.
- `UI.OpenExclusive(gameObject)` — closes all other registered panels, then activates this one.

Each panel's `Toggle()` calls `UI.OpenExclusive(gameObject)` when opening, and `SetActive(false)` when closing.

**To add a new exclusive panel**: call `UI.RegisterExclusive(gameObject)` in `Awake`/`Start`, and replace any `SetActive(true)` in the toggle path with `UI.OpenExclusive(gameObject)`.

## Recipes panel (master-detail)

`RecipePanel` is a full-screen exclusive panel showing every unlocked recipe. Layout is
**master-detail**: a grouped, scrollable list on the **left** (the in-scene `RecipeScroll`,
narrowed to a fixed `LeftWidth` column in code) and a **detail pane on the right**.

**Left list** — recipes are grouped by workstation (`Recipe.tile`) into collapsible
`RecipeGroupDisplay`s (header: building icon + `name (N)`; click toggles). Both the group
and its rows are editor-authored prefabs (`Resources/Prefabs/RecipeGroup` +
`RecipeListRow`, wired to `RecipePanel.recipeGroupPrefab` / `recipeRowPrefab`, Resources
fallback if unwired) — tweak header layout / icon size / row style in the editor, not code.
A group lazily instantiates rows only when first expanded — so a collapsed panel costs ~one
header per workstation, not a card per recipe. Each row is `[output icon][name][inline
On/Off icon]` + a behind-highlight; `Setup` just binds the payload. Clicking the row body
selects it, the On/Off icon toggles allow in place. A thin `Sprites/Misc/divider` separates
each group.

**Detail pane** — on select, a fresh `RecipeDisplay` card (the prefab) is instantiated into
the detail container showing inputs/outputs (live have-amounts), job, a conditions line
("needs `<research>`" + workload), and the On/Off toggle. Fresh instance per selection —
`RecipeDisplay.Setup` assumes a clean card. The detail container is an editor-authored
scene object wired to `RecipePanel.detailPane` (tweak its size/position/background in the
editor); if unwired, `BuildLayout` builds one in code as a fallback.

**Early-game toolbar gates** — two toolbar buttons hide themselves until there's a reason
to use them (each drives a `CanvasGroup`, kept active so it can re-show; both auto-add the
required `CanvasGroup`). Absent buttons here are by design, not a bug:
- `RecipeToggle` → `RecipeButtonGate`: shown once `RecipePanel.CountCraftingStations() >=
  minStations` (default 1). A station is a workstation with a visible recipe that *has
  inputs* — so input-less utility recipes (pump, dig) don't count. `CountCraftingStations`
  reuses `Rebuild`'s render filters (keep them in lockstep).
- `ResearchToggle` → `BuildingBuiltGate` (`buildingName="laboratory"`): shown once that
  building is *built* (`GetByType` count > 0; blueprints under construction don't count).
  `BuildingBuiltGate` is generic — reuse it to gate any button on a building existing.

**Allow dispatch**: every row (craft, processor, or the book proxy) routes through
`RecipePanel.IsAllowed(id)` / `SetAllowed(id, …)` → `disabledRecipes` (by id). Processor
recipes are ordinary `Recipe`s, so a disabled one is skipped at fill time by
`PickProcessorRecipe`; the book proxy uses the `BookProxyRecipeId` sentinel. Icons:
`Sprites/Misc/check` / `redx` at **native 11×11** (do not scale — keeps the pixel art crisp).

**Special display cases**: book-writing recipes (output `ItemClass.Book`) collapse into one
"write a book" proxy whose toggle drives all book recipes; processor recipes (a `Recipe` with a
`duration`) appear under their building with a `FormatDuration at T°` header (e.g. `2 days at 25°`);
`hidden`-flagged recipes (dig/mine/
wheel) are omitted entirely. Expanded-group state persists (`expandedRecipeGroups`); see
SPEC-data.md for the recipe/process panel data notes. Layout reveals use
`LayoutUtil.RebuildImmediate` (see above) to avoid the min-height pop.

## Sub-canvases (mesh-rebuild localization)

The root `UI` GameObject has a single `Canvas` component (`ScreenSpaceOverlay`) that all panels live under. Without intervention, **every** UI widget change — an item count text updating, a fillbar tick, a toast fade — invalidates the root canvas's mesh and forces a rebuild of the entire UI hierarchy (~600 active widgets in a typical play state). This is purely a CPU cost, not a draw count cost; the draw count stays roughly the same, but the per-frame work to regenerate the canvas mesh balloons with hierarchy size.

**Pattern:** add a `Canvas` + `GraphicRaycaster` component to a panel root. The panel becomes a "sub-canvas" — its mesh rebuilds stay local to its subtree and don't dirty siblings. With default settings (`overrideSorting = false`, `renderMode = ScreenSpaceOverlay` to match root) the sub-canvas inherits sort order from the root canvas, so there's no visual change.

**Current sub-canvases** (all added 2026-05-26): `InventoryScroll`, `BuildPanel`, `JobsScroll`, `AlertToast`, `ChatPanel`. Each was chosen because either (a) it contains many widgets (InventoryScroll has 773 in the typical state), or (b) it updates frequently (toast fades per-frame, chat receives messages, jobs change).

**When to add one**: a panel either holds a large widget subtree OR has frequent per-frame mesh changes that don't need to propagate to siblings. Don't add one per widget — each sub-canvas is its own batching domain, and over-fragmenting trades rebuild cost for batching cost.

**Adding via MCP**: see `Components/GpuStatsHUD.cs` session notes for the live-API pattern — `AddComponent<Canvas>` + `AddComponent<GraphicRaycaster>` + force `renderMode = ScreenSpaceOverlay` (Unity sometimes defaults a nested Canvas to WorldSpace) + `MarkSceneDirty`. Don't add via direct `.unity` YAML write — clobbers unsaved editor state.

## Esc key chain

All Esc handling lives in `UI.Update()` — centralised so a single press never collapses two layers in the same frame. Priority (first match wins, rest of the chain is ignored for that press):

1. `SaveMenuPanel` active → `SetActive(false)` (most modal — full-screen overlay)
2. `BuildPanel.IsSubPanelOpen` → `CloseSubPanel()`
3. Any registered exclusive panel currently active → `SetActive(false)` on the first one found
4. `MouseController.mouseMode != Select` → `SetModeSelect()`

`MouseController` no longer reads `KeyCode.Escape` directly.

## LMB-on-world chain

Left-click on the world (LMB outside UI, i.e. `!IsPointerOverGameObject()`) mirrors Esc steps 1–3, with a narrowed step-4 living in `MouseController` instead. Two halves run in the same frame:

**`UI.Update()`** — non-consuming Esc-mirror, first match wins:
1. `SaveMenuPanel` active → close.
2. `BuildPanel.IsSubPanelOpen` → `CloseSubPanel()`.
3. Any active exclusive panel → close it.

The block falls through (no `return`); `MouseController` still processes its own LMB handling for the same press. So a Select-mode click both closes a panel **and** selects the clicked tile.

**`MouseController.Update()` Build branch** — narrowed step 4:
- Build mode with `structType == null` ⇒ the click has no tool meaning. `SetModeSelect()`, then seed `_dragStartScreenPos` / `_dragStartedInMode = Select` so the same mouse-down can drag-select on release without lifting first.
- Build mode with `structType` set, `Harvest`, and `Remove` modes are unchanged — clicks always run their tool action; if a panel is open it also closes via the `UI.Update` block above.

## UI scaling & text crispness

The whole UI scales via one knob and stays crisp at any scale. Four pieces work together:

**UI scale slider** — `SettingsManager.uiScale` (1.0–2.0, default 1.0) is applied as the root
`CanvasScaler.scaleFactor` (Constant Pixel Size mode) by `UI.ApplyUiScale` on
`SettingsManager.OnChanged`. One knob scales every widget/font/icon — no per-element work,
because everything is authored in canvas reference units. The slider snaps to `UiScaleStep`
(0.05 = 2.5%, so 75% = 1.5×) and applies on **release**, not during drag (`SliderReleaseRelay`
on the slider GO): rescaling the canvas mid-drag would shove the handle out from under the cursor.

**SDF font** — every selectable UI font is an **SDF** asset (never a bitmap), so text stays sharp
at non-integer scales (a bitmap atlas only renders crisply at integer multiples). The shipped
default is **Figtree SDF @11** ("Smooth"); m5x7 SDF @16 ("Pixel") is the alternate (see the font
switcher below, and `TMP_Settings.defaultFontAsset` = the shipped default). `FontConfig.asset` is
the editor-side source of truth (font + size + `primaryTextColor`, currently Figtree @11); its
"Apply to All" tool (`FontConfigEditor`) propagates to every TMP in scenes + prefabs, **including**
TMP_InputField's `m_GlobalFontAsset`. Each SDF font's embedded material should have `_Sharpness =
1.0` (hardens SDF edges — the single biggest crispness win); for the m5x7 SDF "Pixel" font,
`faceInfo.lineHeight = 105.875` makes line advance exactly 14px at size 16. NB: a font reimport
resets lineHeight/sharpness.

**Pixel snap** (`UITextRuntimeStyle.cs`) — SDF renders each sub-pixel baseline phase with different
blur, so identical labels at different fractional Y looked inconsistent ("messy"). This snaps each
text **line's** baseline to the nearest whole device pixel by shifting that line's vertices
uniformly — per-line (not per-vertex, so glyph shapes stay intact; per-line because line advance
isn't a whole number of device px at non-integer scale). Event-driven on TMP's `TEXT_CHANGED`
(static text costs nothing), overlay-canvas only (world text untouched), self-bootstraps via
`RuntimeInitializeOnLoadMethod`. **`Canvas.pixelPerfect` does NOT work here** — it doesn't snap
TMP vertices (verified); don't reach for it.

**Icon filtering** — pixel-art icons can't go SDF, so at non-integer scale Point filtering makes
them uneven. The `ItemIcons` atlas is **Bilinear** (smooth at any scale; UI-only). Sliced
frames/buttons (`UIChrome` atlas) stay **Point** — Bilinear bleeds across 9-slice seams. Shared
world atlases (Buildings, Plants…) stay Point: the world renders at integer scale where
Bilinear==Point, and flipping them risks sub-pixel blur there.

## In-game font switcher

Players pick a UI font in Options. `UIFontOptions.asset` (Resources) is the registry — entries
of `{name, TMP_FontAsset, fontSize}` (each font needs its own size to match apparent size; e.g.
m5x7@16 vs Figtree@11). `SettingsManager.uiFontIndex` (PlayerPrefs) is the selection; the
OptionsPanel `fontDropdown` drives it; `UITextRuntimeStyle` applies it at runtime by stamping the
chosen font/size onto every overlay label (existing + dynamically-spawned, via the TEXT_CHANGED
hook), plus a strong full refresh (`RefreshAll`: re-font + `ForceMeshUpdate` + layout rebuild) at
startup and on a font-choice change. New content born in the prefab-baked font is corrected
**in-frame**: the TEXT_CHANGED hook re-fonts it and regenerates synchronously (inline
`ForceMeshUpdate`). TEXT_CHANGED fires during the canvas pre-render pass — after the wrong-font
mesh is committed but before the GPU draws — so the corrected mesh replaces it the same frame
(no one-frame flicker, no deferred/debounced pass).

**Hard-won lessons (don't relearn these):**
- **Two TMP fonts coexisting in one canvas garbles** (mixed atlases) — the runtime swap MUST reach
  100% of labels or the partial mix corrupts. This was the root of a long garble chase.
- **Selectable fonts must be Static SDF assets, not Dynamic** — a Dynamic atlas is unstable for
  runtime-cloned text and renders garbage. Bake via `TMP_FontAsset.TryAddCharacters(asciiSet)` +
  set `atlasPopulationMode = Static` (keep the asset GUID so refs survive).
- Startup text can render with stale/invisible meshes; the `RefreshAll` passes fix it.
- **The flicker was killed by in-frame regen, NOT by going font-agnostic.** The font-agnostic plan
  (strip all TMP to `font = null` → fall back to `TMP_Settings.defaultFontAsset` → content born in
  the chosen font) was found **infeasible under TMP 3.0.6 / Unity 2021.3** (TMP is now bundled in
  `com.unity.ugui` 2.0; whether 6.3's TMP changed this is unverified): `TMPro_UGUI_Private.LoadFontAsset`
  coerced `m_fontAsset == null → defaultFontAsset` on every validate/awake/setter, so you could not
  *persist* a null font. Verified: `LoadPrefabContents` → set null → `SaveAsPrefabAsset` writes the
  concrete default GUID, not `fileID: 0`. Only raw YAML `{fileID: 0}` writes can store null, and they
  re-bake silently on any later prefab edit (fragile forever). Don't re-attempt this.

## Help hoverables

Standardized "?" help icons sharing one sprite (`helpicon.png` → TMP sprite asset
`Assets/Resources/Sprites/misc/HelpIcons.asset`, glyph named `help`). Two surfaces:

- **Panel-header "?"** — a GameObject named `InfoButton`: a `TextMeshProUGUI` whose text is
  `<sprite name="help">` (with `spriteAsset = HelpIcons`) plus a `Tooltippable`, anchored
  top-right. Copy is the Tooltippable's title/body (static in-scene, or set live in code — see
  `GlobalHappinessPanel.populationInfoTip`). To add one, clone an existing `InfoButton`.
- **Inline (InfoView text blobs)** — the mouse/building/tile views render one TMP string, so
  help sits inline: `Help.Icon("key")` emits `<link="help:key"><sprite name="help"></link>`
  after a stat line. `InfoTextHover` (auto-added to each InfoView's text in its `Awake`)
  hit-tests the hovered link via `FindIntersectingLink` and drives `TooltipSystem`. Copy lives
  in the `Help` registry. To add one: add a `Help` entry + an `Icon("key")` call in the view.

The help sprite asset is assigned **per-component** (InfoTextHover sets `text.spriteAsset`;
InfoButtons set it directly) — it is deliberately **not** TMP's project-wide default sprite
asset. `helpicon.png` is 13×13 with a 1px transparent margin (for bilinear edge sampling), but
the TMP glyph's `m_GlyphRect` is `(1,1,11,11)` — it captures only the centered 11px disc, so
metrics stay 11px and placement is unchanged. If you re-export `helpicon.png`, keep the glyphRect
tracking the disc bounds (margin offset, disc size), not the full texture. Tooltip bodies cap at `TooltipSystem.MaxBodyWidth` (350px) and word-wrap; short ones
still hug their text.

## Key Files

| File | Role |
|------|------|
| `Assets/UI/Help.cs` | Help-tooltip copy registry + `Icon(key)` inline markup + cached help sprite asset |
| `Assets/UI/InfoTextHover.cs` | Hit-tests inline help `<link>`s in an InfoView text blob → TooltipSystem |
| `Assets/UI/UITextRuntimeStyle.cs` | Runtime UI text manager: applies the player font choice (in-frame regen for new content + strong refresh on switch/startup) AND per-line baseline pixel-snap (self-bootstraps). |
| `Assets/UI/UIFontOptions.cs` | Player-selectable font registry (`Assets/Resources/UIFontOptions.asset`) — `{name, font, size}` entries |
| `Assets/UI/FontConfig.cs` + `Assets/Editor/FontConfigEditor.cs` | Editor-baked font/size/primary-color source of truth + "Apply to All" propagation + `pixelSnap` toggle |
| `Assets/Components/SliderReleaseRelay.cs` | Fires once on slider pointer-up (UI-scale applies on release) |
| `Assets/Controller/InventoryController.cs` | Global panel, selection routing, discovery, targets |
| `Assets/Components/CollapsibleHeader.cs` | Reusable header row that collapses/expands later siblings; used by inventory + jobs panels |
| `Assets/UI/ItemDisplay.cs` | Row prefab component (tree collapse, targets, allow toggle) |
| `Assets/UI/StoragePanel.cs` | Storage detail panel (slot view + allow tree; handles liquid storage too) |
| `Assets/UI/GlobalInventoryPanel.cs` | Full-screen exclusive detail panel: per-item location breakdown + target editing + don't-consume toggle |
| `Assets/UI/InventoryDetailRow.cs` | One row in GlobalInventoryPanel (prefab `Resources/Prefabs/InventoryDetailRow`) — flat-list, indent-for-depth |
| `Assets/UI/InventoryBar.cs` | The row's "where" distribution bar — colored location segments + deficit/target marker, per-segment tooltips |
| `Assets/Components/StorageSlotDisplay.cs` | Compact slot row text display |
| `Assets/Model/Inventory.cs` | Data model (itemStacks, allowed dict, InvType) |
| `Assets/Model/GlobalInventory.cs` | Global quantity totals |
| `Assets/UI/InfoPanel.cs` | Tabbed info panel container (selection → tabs → sub-views) |
| `Assets/UI/InfoViews/StructureInfoView.cs` | Structure/blueprint info + enable/disable, priority, worker controls |
| `Assets/UI/InfoViews/AnimalInfoView.cs` | Single animal info display (spawns SkillDisplay widgets) |
| `Assets/Components/SkillDisplay.cs` | Skill icon + level + XP bar widget (anchor-based fill, Tooltippable on icon + bar hitbox) |
| `Assets/Components/FillBar.cs` | Reusable fill bar (0–1 fraction → fillAmount); used by HappinessNeedRow |
| `Assets/Components/ComfortBar.cs` | Range bar: comfortable band (green) vs stalled (yellow) over a fixed domain + circle marker for current value + hover readout; used by StructureInfoView (plant temp/moisture) and AnimalInfoView (mouse temp) |
| `Assets/UI/GlobalHappinessPanel.cs` | Exclusive panel: colony happiness overview + per-need breakdown |
| `Assets/UI/HappinessNeedRow.cs` | One need row: name, count, fill bar, avg value |
| `Assets/UI/InfoViews/TileInfoView.cs` | Tile-only info (coords, water, floor inv) |
| `Assets/Model/SelectionContext.cs` | Structured selection data (tile + structures + blueprints + animals) |
