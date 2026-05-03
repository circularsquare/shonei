# SPEC — Unity Editor work via MCP

This is the playbook for editor-side work — scene mutations, UI building, runtime
inspection — done through the `mcp__unity__*` tools. **Read this before doing
any MCP work.** Most of the "Unity work" on this project is UI testing, scene
inspection, and small in-editor prototypes; full system changes still go through
script edits with the regular Edit/Write tools.

## What MCP is for, and isn't for

| Use MCP for | Don't use MCP for |
|-------------|-------------------|
| Inspecting the live scene (find_gameobjects, gameobject/{id} resources) | Editing script files — use Edit/Write so the user sees the diff |
| Building or tweaking UI hierarchies in the editor (panels, buttons, layouts) | Writing `.unity` or `.prefab` files **directly** as YAML — see the safety section below |
| Reading/clearing the console (`read_console`) | Long-running jobs that block the editor — prefer asking the user to run them |
| Running EditMode/PlayMode tests (`run_tests` + `get_test_job`) | Mass programmatic edits where the diff is the whole story — Edit is more reviewable |
| Quick `execute_code` snippets to inspect or mutate live objects | Persisting authored content (items, recipes, plants) — that's JSON in `Assets/Resources/` |
| Triggering compilation (`refresh_unity`) | Anything where in-Unity Inspector tweaking is faster than coding |

The blast radius of API-driven mutations is "the live editor session" — Ctrl+Z
generally works, and changes don't persist until the user saves the scene.
That's a *much* lower-stakes operation than writing a YAML file from the outside.

## Safety: what's actually risky

Two distinct scenarios with very different risk profiles:

**Safe (live-API mutations).** `manage_gameobject create`, `manage_components`,
`execute_code`, `manage_ui`, etc. all go through Unity's runtime API and
modify the in-memory editor state — exactly like the Inspector does. The user
sees the changes immediately, can Ctrl+Z, and unsaved scene work is **not at
risk** because the .unity file isn't being touched. Use these freely for editor
work.

**Risky (direct file writes).** Tools or workflows that rewrite `.unity` /
`.prefab` files on disk by parsing-and-serializing YAML. These read the
*on-disk* version, ignore Unity's in-memory state, and clobber unsaved work
on next reimport. **Avoid these** unless the user has explicitly saved
everything first. (We lost ~3 hours of UI work to this on 2026-03-23.)

Practical workflow:
1. Before scene mutations, ask once: "OK to make in-editor changes? Ctrl+S
   first if you have unsaved work." Don't ask every turn — once per session
   is enough.
2. Use `manage_editor stop` before scene-mutating `execute_code` if Unity is
   in Play mode (Play-mode changes revert; `MarkSceneDirty` also fails).
3. Don't `manage_scene save` without an explicit ask. Let the user save when
   they like the result.
4. After every non-trivial Edit/Write to a `.cs` file, `read_console` before
   moving on — compile errors lock the MCP bridge.

## Common MCP gotchas

- **Roslyn unavailable** → `execute_code` falls back to **CodeDom (C# 6)**.
  No local functions; use `Func<>` / `Action<>` lambdas (recursive lambdas
  via the `Action foo = null; foo = (x) => …foo(…)…;` pattern).
- **`GameObject.Find` skips inactive objects.** Most exclusive panels are
  inactive by default. Search via the UI canvas:
  `foreach (var tr in ui.GetComponentsInChildren<Transform>(true)) if (tr.name == nm) …`
- **Play mode reverts scene changes.** Always `manage_editor stop` first if
  you're about to mutate scene state. `MarkSceneDirty` also throws in Play
  mode.
- **`refresh_unity` after script edits** — domain reload may need a kick.
  Then `read_console` to confirm clean compile before relying on new types.
- **CodeDom string interp** (`$"..."`) works fine, but multiline interpolations
  with `:format` specifiers can confuse it — prefer `string.Format` or
  `.ToString("0.00")` for safety.
- **`UnityEditor.Events.UnityEventTools.AddPersistentListener`** is how you
  wire onClick to a method on a specific component instance from code.
  Pair with `RemovePersistentListener` when re-wiring after destroying the
  target.
- **Multi-Unity instances**: if MCP errors with "multiple connected, no
  active instance set", call `set_active_instance` once with `Name@hash`.

## UI style conventions

These are the project's existing conventions. **Match them.** Anything new
should look at home next to ResearchPanel / TradingPanel / RecipePanel.

### Canvas / pixel scale

- Reference resolution: **960 × 540** (16:9 half-1080p)
- Canvas Scaler: Scale With Screen Size, **160 reference pixels per unit**
- Pixel Perfect Camera: assets at **16 PPU** (1 art-pixel = 10 canvas-pixels
  at 1× scale). Sprite import uses Point filtering.
- Implication: prefer integer sizes that play well with this scale. Multiples
  of 8 are usually safe; 16 is the cleanest "art tile" unit.

### Font

- **Default font asset: `m5x7 SDF`** (TextMeshPro/Resources/Fonts &
  Materials/m5x7 SDF.asset). Pixel font; renders crisply only at the sizes
  it was designed for.
- **Use these font sizes only, in this order of preference:**
  - **16** — section headers / titles
  - **14** — body / labels
  - **12** — small / annotations
- Don't use 18, 20, 22 — they don't snap to the bitmap grid and render
  fuzzy. If you need bigger, jump to 24 or 32.
- Default text color: **`Color.black` (0,0,0,1)**. Existing panels are ~95%
  black; white-on-light-bg is a common bug from forgetting to set the
  color. If you create a new TMP component, set the color explicitly the
  same line you set the text.
- Unity's TMP default-color knob doesn't safely exist project-wide
  (multiplying with the font asset's face color breaks intentional colored
  text). Just always set it.

### Spacing / layout

| Setting | Value |
|---------|-------|
| VerticalLayoutGroup `spacing` | **2** (rows) or **4** (sections) |
| HorizontalLayoutGroup `spacing` | **8** |
| Panel full-screen margin | **20 px** |
| Standard row height | **16** (tight) or **24** (comfortable) |
| Section gap (spacer) | **6–8** |

For nested layout groups, set **`childControlWidth = childControlHeight = true`**
and **`childForceExpandHeight = false`**, with explicit `LayoutElement`
preferred heights on each child. (Children with no LayoutElement and a
parent VLG that doesn't control height will collapse or overlap — that's
the bug that produced the first OptionsPanel build.)

### Sprite reuse — check before creating new

Before adding any new UI sprite, search `Assets/Resources/Sprites/Misc/`. The
project already has these primitives — **copy them, don't replace them**:

| Need | Use this |
|------|----------|
| Panel background (wood) | `woodframe.png` (sliced, 2px border) |
| Panel background (alt) | `grassframe.png`, `blueprintframe.png`, `bpdeconstructframe.png` |
| Generic button | `button.png`, `buttonpressed.png` (pressed state) |
| Increment / decrement | `buttonplus.png`, `buttonminus.png` |
| Checkbox / toggle checkmark | `check.png` |
| Close button glyph | `x.png` (generic), `redx.png`, `yellowx.png` |

**If a needed widget doesn't have a sprite yet** (e.g. sliders, dropdowns —
both still using Unity's default gray skin), build with placeholders **and
flag it explicitly** to the user as "needs custom asset." Don't silently
ship default-skinned widgets pretending they're done.

### Color palette (text + state)

| Use | Color |
|-----|-------|
| Body / heading text | `(0, 0, 0, 1)` — black |
| Secondary text | `(0.20, 0.20, 0.20)` — dark gray |
| Disabled / inactive | `(0.7, 0.7, 0.7, 0.8)` — light gray |
| State: completed | `(0.45, 0.70, 0.45, 0.6)` — green tint |
| State: in-progress | `(0.85, 0.75, 0.30, 0.55)` — amber tint |
| State: locked / unbuilt | `(0.5, 0.5, 0.5)` — gray |

### Panel architecture

- Exclusive panels register in `Awake()` via `UI.RegisterExclusive(gameObject)`,
  open via `UI.OpenExclusive(gameObject)` from `Toggle()`. Default state is
  inactive (`gameObject.SetActive(false)` in Awake or in the editor).
- Panel root is a child of the `UI` Canvas (top-level scene object). Don't
  create a new canvas.
- Toggles (top-bar buttons) are siblings of panels under the same canvas,
  with their `onClick` wired to `<PanelName>.instance.Toggle()` as a
  persistent UnityEvent listener.
- See [SPEC-ui.md](SPEC-ui.md) for the broader UI architecture (InfoPanel
  tab system, ItemDisplay routing, etc.).

## Workflow recipes

**Add a new exclusive panel via MCP** (e.g. SettingsPanel, KeybindingsPanel):
1. Create the script (`Assets/UI/<Name>.cs`) using `ResearchPanel.cs` as the
   template — Edit/Write, not MCP.
2. `read_console` to confirm clean compile.
3. `manage_editor stop` if Play mode is active.
4. `execute_code` (codedom) that:
   - Finds the `UI` canvas
   - Builds the panel hierarchy with `DefaultControls` / `TMP_DefaultControls`
     plus your own `Image` + `LayoutElement` configuration
   - Adds the script via `gameObject.AddComponent(scriptType)` (look up
     `scriptType` by iterating `AppDomain.CurrentDomain.GetAssemblies()`)
   - Wires `[SerializeField]` refs via `UnityEditor.SerializedObject`
     and `FindProperty(...).objectReferenceValue = …`
   - Calls `MarkSceneDirty`
5. Add a top-bar toggle button — same canvas, woodframe sprite, TMP label,
   `onClick` wired via `UnityEventTools.AddPersistentListener`.
6. Tell the user the panel exists and is inactive; they can Play-mode-test
   and save.

**Iterate on UI sizing/spacing** without rebuilding:
- Find the panel root, then iterate `GetComponentsInChildren<LayoutElement>(true)`
  and adjust by name. Call `LayoutRebuilder.ForceRebuildLayoutImmediate(rt)`
  so the next screenshot reflects the change.

**Test a panel without entering Play mode**:
- Editor mode doesn't run Awake/Start, so `UI.RegisterExclusive` won't have
  fired and `Toggle()` is unavailable. Use `gameObject.SetActive(true)` /
  `false` directly in the inspector or via `execute_code`.

## Known limitations

- `manage_ui` is for **UI Toolkit (UXML)**, not uGUI. The project uses uGUI
  (Canvas + Image + TMP); use `manage_gameobject` + `manage_components` +
  `execute_code` instead.
- `manage_camera screenshot` may not capture overlay-rendered Canvas content
  the way the user sees it; for visual review, ask the user for a screenshot.
