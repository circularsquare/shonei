# SPEC-ui — Inventory UI Panels

## Overview

The inventory UI uses a **split-panel approach**: a global panel that's always visible, and a separate storage panel that appears when a storage or liquid inventory is selected. Market inventories temporarily overwrite the global panel (to be reworked later).

```
Canvas
├── Global Panel (always visible)
│   ├── Title ("town" or market name)
│   └── ItemDisplay rows (collapsible tree, quantities, targets)
│
└── StoragePanel (visible when storage/liquid selected)
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
| Storage / Liquid | Global quantities + targets (unchanged) | Shown with inv details |
| Market | Market quantities + targets (title: market name) | Hidden |

Triggered by `MouseController` on left-click: storage/liquid/market tiles call `SelectInventory(tileAt.inv)`, everything else calls `SelectInventory(null)`.

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

- Toggle click → `ItemDisplay.OnClickAllow()` → `inv.ToggleAllowItem()` or `ToggleAllowItemWithChildren()` (for parent groups)
- Disallowing an item with existing quantity triggers `RegisterStorageEvictionHaul` (p3 haul work order)
- Auto-allow parent: if all discovered siblings become allowed, the parent group is auto-enabled
- AllowAll / DenyAll buttons wired to `StoragePanel.OnClickAllowAll/DenyAll`
- Copy/paste filters: Shift+LMB on storage = copy, Shift+RMB = paste (handled by `MouseController` → `InventoryController.CopyAllowed/PasteAllowed`)

## Key Files

| File | Role |
|------|------|
| `Assets/Controller/InventoryController.cs` | Global panel, selection routing, discovery, targets |
| `Assets/UI/ItemDisplay.cs` | Row prefab component (tree collapse, targets, allow toggle) |
| `Assets/UI/StoragePanel.cs` | Storage/liquid detail panel (slot view + allow tree) |
| `Assets/Components/StorageSlotDisplay.cs` | Compact slot row text display |
| `Assets/Model/Inventory.cs` | Data model (itemStacks, allowed dict, InvType) |
| `Assets/Model/GlobalInventory.cs` | Global quantity totals |
