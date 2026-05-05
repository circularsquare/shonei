# SPEC — Unity Editor work via MCP

> **Permission gate.** MCP scene-mutating tools require explicit per-task
> permission from the user — see CLAUDE.md. Read-only inspection is fine
> any time, but actual mutations (`execute_code` edits, `manage_gameobject`
> create/modify, `manage_components` add/set_property, etc.) need a green
> light. **Default is no.** This spec covers *how* to do MCP work cleanly
> once permission is granted; it does not authorize MCP use.

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
4. After **non-trivial** Edit/Write to a `.cs` file, `read_console` before
   moving on — compile errors lock the MCP bridge. **Skip for tiny low-risk
   edits** (one-line filters, string-literal tweaks, magic-number changes, line
   deletions): `refresh_unity` + `read_console` round-trips cost real time and
   add little value when there's no plausible compile risk. Run them when the
   edit could reasonably break compile — new method, signature change,
   reflection/framework API, new `using`, etc.

## Common MCP gotchas

- **Roslyn unavailable** → `execute_code` falls back to **CodeDom (C# 6)**.
  No local functions (`string Foo() { … }` inside another method body is a
  parse error) — use `Func<>` / `Action<>` lambdas (recursive lambdas via
  the `Action foo = null; foo = (x) => …foo(…)…;` pattern). CodeDom also
  doesn't auto-import namespaces: fully qualify UI types
  (`UnityEngine.UI.Image`, `UnityEngine.UI.Slider`,
  `UnityEngine.UI.Image.Type.Sliced`) or compile fails.
- **`GameObject.Find` skips inactive objects.** Most exclusive panels are
  inactive by default. Search via the UI canvas:
  `foreach (var tr in ui.GetComponentsInChildren<Transform>(true)) if (tr.name == nm) …`
- **Duplicate GameObjects with the same name.** When scenes accumulate
  iterations (e.g. rebuilding a panel without deleting the old one),
  multiple GameObjects can share a name. The naive
  `foreach { if (name == X) break; }` pattern grabs the *first* match —
  which may be an inactive orphan. Always: (a) collect *all* matches,
  (b) prefer the one with `activeSelf == true` if applying user-visible
  changes, (c) flag duplicates back to the user. Strong scene-side signal:
  a `Debug.LogError("two Xs!")` in the panel's `Awake()` (the project's
  exclusive-panel pattern logs this) — check the console history if a
  visual change you applied seems to have no effect.
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
- **Slider handle defaults are wrong twice.** `DefaultControls.CreateSlider`
  produces a handle that (a) stretches vertically and (b) overshoots the
  ends of the slider when at min/max. Both need fixing for any custom
  slider sprite to look right.

  **Vertical stretch:** Handle's perpendicular-axis anchor is set to
  `(0,0)`/`(0,1)` — "stretch to fill." Slider drives *only* the slide-axis
  anchor at runtime, so the stretch sticks. Collapse the perpendicular
  anchor to a single point (0.5 for horizontal sliders) and snap to
  native sprite size.

  **Edge overshoot:** Handle Slide Area is stretched flush to the
  slider's edges, so at max value the handle's *center* sits at the edge
  and half the handle overflows. Inset the slide area by `handleWidth/2`
  on each end of the slide axis.

  Combined fix for a horizontal slider:
  ```csharp
  var hRt = slider.handleRect;
  // (a) stop vertical stretch + use native sprite size
  hRt.anchorMin = new Vector2(hRt.anchorMin.x, 0.5f);
  hRt.anchorMax = new Vector2(hRt.anchorMax.x, 0.5f);
  hRt.GetComponent<UnityEngine.UI.Image>().SetNativeSize();
  // (b) inset slide area so handle stops at the edge, not past it
  // ONLY touch the X axis — wholesale `slideArea.anchorMin = Vector2.zero;
  // anchorMax = Vector2.one;` would also reset the Y axis and undo any
  // manual height customization on the slide area.
  var slideArea = hRt.parent as RectTransform;
  float half = hRt.sizeDelta.x * 0.5f;
  var aMin = slideArea.anchorMin; aMin.x = 0; slideArea.anchorMin = aMin;
  var aMax = slideArea.anchorMax; aMax.x = 1; slideArea.anchorMax = aMax;
  var oMin = slideArea.offsetMin; oMin.x =  half; slideArea.offsetMin = oMin;
  var oMax = slideArea.offsetMax; oMax.x = -half; slideArea.offsetMax = oMax;
  ```
  For vertical sliders: swap axes (collapse X anchor to 0.5, inset on
  `offsetMin/Max.y`, preserve X axis on the slide area).

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
  Materials/m5x7 SDF.asset). Pixel font; only renders crisply at specific
  sizes.
- **`fontSize = 16`. Period.** Anything smaller (12, 14) is illegible at
  this project's canvas scale. **Don't introduce visual hierarchy via bold
  or uppercase** — they don't render well in `m5x7`. Default to flat
  sizing across a panel (titles, headers, row labels all at 16). If you
  really need contrast, use color (the secondary-text gray) — not weight
  or case. If you genuinely need bigger (rare — splash titles, etc.), use
  a multiple of 16: 32 or 48.
- Default text color: **`Color.black` (0,0,0,1)**. White-on-light-bg is a
  common bug from forgetting to set the color when creating a TMP
  component. Set the color the same line you set the text.
- Unity's TMP default-color knob doesn't safely exist project-wide
  (multiplying with the font asset's face color breaks intentional colored
  text). Just always set it.

### Spacing / layout

**Be compact by default.** Pad to the smallest dimension that actually fits
the rendered content. A row with `fontSize=16` text needs ~16 px of height,
not 24. A label saying "Ambient" needs ~80 px of width, not 110.
Over-reservation makes panels feel sparse, pushes the panel size larger than
it needs to be, and at 960×540 reference resolution any extra padding
dominates the screen quickly. **Default direction: smaller. Only relax if
something is actually clipped or cramped.**

| Setting | Value |
|---------|-------|
| VerticalLayoutGroup `spacing` | **2** (rows) or **4** (sections) |
| HorizontalLayoutGroup `spacing` | **8** |
| Panel full-screen margin | **20 px** |
| Single-line TMP text — `LayoutElement.preferredHeight` | **14** |
| Row height (label + control) | **16** |
| Section gap (spacer) | **6–8** |
| Label `minWidth` in a row | Fit longest actual label, not a round number. For typical 1-word labels at fontSize 16, **~80** is plenty. |

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

**Respect native sprite size for `Image.Type = Simple` widgets.** Simple-mode
Images stretch to fill their RectTransform — pixel art looks awful when
stretched (handles become pills, icons go blurry, dropdown arrows skew).
For Simple sprites: read `sprite.rect.size` and set `RectTransform.sizeDelta`
to match (handles, dropdown arrows, checkmarks, item icons). For `Sliced`
sprites only the borders are protected; the middle stretches — fine for
backgrounds, but the rect's *minor axis* should still be ≥ 2× the border
(otherwise borders eat the whole sprite and there's nothing to stretch).
Watch out for parent stretching: a Slider's Handle defaults to vertically-
stretched anchors (`anchorMin.y=0, anchorMax.y=1`), and **`Slider.UpdateVisuals`
resets these every frame** — it rebuilds anchors from `Vector2.zero` /
`Vector2.one` and only overwrites the parallel (value) axis, so trying to
pin the handle to centre-V via anchors **won't survive runtime**. To get a
fixed-height handle: set the **parent** Handle Slide Area to the target
height (`sizeDelta.y = N` with centre-V anchors) and set the Handle's
`sizeDelta.y = 0` so it stretches to match its 8-tall parent. Width is
safe — Slider only drives the parallel-axis anchors based on value.

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
4. `execute_code` (codedom) that builds the panel and wires it (see skeleton
   below).
5. Add a top-bar toggle button — same canvas, woodframe sprite, TMP label,
   `onClick` wired via `UnityEventTools.AddPersistentListener` (also in the
   skeleton).
6. Tell the user the panel exists and is inactive; they can Play-mode-test
   and save.

**Non-obvious bits the codedom (C# 6) `execute_code` flow needs.** Use `ResearchPanel.cs` / `OptionsPanel.cs` as the script template and read existing panels for the standard Canvas/Image/RectTransform setup. The points below are the ones that aren't discoverable from looking at a finished panel:

- **Sprite resources for DefaultControls.** `DefaultControls.CreateButton/Slider/Toggle` and `TMP_DefaultControls.CreateDropdown` need a populated `DefaultControls.Resources` struct with built-in skin sprites loaded via `UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd")` and friends (Background, InputFieldBackground, Knob, Checkmark, DropdownArrow, UIMask). Override per-widget with project sprites (woodframe, check, x) where possible.
- **Type lookup**. Project assemblies load with no fixed `AssemblyName`, so `Type.GetType("MyPanel")` returns null. Iterate `System.AppDomain.CurrentDomain.GetAssemblies()` and call `asm.GetType("MyPanel")`.
- **SerializeField wiring**. `var so = new UnityEditor.SerializedObject(panelScript); so.FindProperty("fieldName").objectReferenceValue = ...; so.ApplyModifiedPropertiesWithoutUndo();`. Misspelled field names return null silently — verify by reading the component back.
- **Initial inactive state.** Panel scripts' `Awake()` registers exclusive but doesn't `SetActive(false)` (look at the existing panels). MCP code must set it after creation.
- **onClick wiring.** Use `UnityEditor.Events.UnityEventTools.AddPersistentListener(btn.onClick, action)` with `Delegate.CreateDelegate(typeof(UnityAction), panelScript, scriptType.GetMethod("Toggle"))`. Persistent listeners survive play/stop and are written to scene data; runtime `AddListener` does not.
- **Dirty the scene.** `EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene())` so Unity prompts the user to save. Throws in Play mode — `manage_editor stop` first.

**Iterate on UI sizing/spacing** without rebuilding:
- Find the panel root, then iterate `GetComponentsInChildren<LayoutElement>(true)`
  and adjust by name. Call `LayoutRebuilder.ForceRebuildLayoutImmediate(rt)`
  so the next screenshot reflects the change.

**"Copy from X to Y" means the whole subtree.** When the user asks to copy
formatting/config from one UI element to another, default interpretation is
every descendant — sprites, RectTransform anchors/offsets/sizeDelta, Image
colors, LayoutElement, etc. Visual styling lives inside the tree, not at
the top. Copying only the outer container's LayoutElement matches sizes
but leaves visual defaults untouched, producing siblings that share
dimensions but look completely different (caught after a slider-copy task
left Sfx/Ambient sliders visually divergent from Master). Cleanest pattern:
`UnityEngine.Object.Instantiate(source.gameObject)` then re-parent and
rewire references — guarantees byte-identical visuals. If iterating
manually, recurse the subtree, don't stop at direct children.

**Test a panel without entering Play mode**:
- Editor mode doesn't run Awake/Start, so `UI.RegisterExclusive` won't have
  fired and `Toggle()` is unavailable. Use `gameObject.SetActive(true)` /
  `false` directly in the inspector or via `execute_code`.

**Build size strategy.** Default: build the panel in one `execute_code` call,
then iterate on user feedback. That works fine for typical-sized panels
(<10 elements, 1-2 levels of nesting) and matches the user's preferred
working style. **For substantially larger panels** (deeply nested layout
groups, dozens of elements), consider splitting: build root + 2-3 elements,
ask for a screenshot, then add the rest. Layout bugs in nested
LayoutGroups compound fast and are much easier to diagnose in small batches.

## Done-check — before saying "complete"

Run through this before telling the user a scene/UI change is done. Don't skip
items just because they "should" pass — the whole point is catching the ones
that quietly didn't.

### Compile + console
- After **non-trivial** script edits: `read_console` returns no errors and
  editor isn't mid-compile (`refresh_unity wait_for_ready=true` returned
  cleanly). For tiny low-risk edits (single-line filters, literal tweaks,
  number changes), skip the round-trip — see "Safety: what's actually risky"
  above.

### For new panels — wiring
- Panel is a child of the `UI` Canvas.
- Panel script registers exclusive in `Awake()`
  (`UI.RegisterExclusive(gameObject)`).
- Panel defaults to inactive (`SetActive(false)` after creation).
- All `[SerializeField]` refs on the script are wired. Verify by reading
  `mcpforunity://scene/gameobject/{id}/component/{ScriptName}` and
  confirming every property shows a non-null `name` + `instanceID`.
- Top-bar toggle button has at least one persistent `onClick` listener
  pointing at `<PanelName>.Toggle` on the panel's GameObject. Verify via
  `GetPersistentEventCount` / `GetPersistentTarget` /
  `GetPersistentMethodName`.

### For style
- All TMP text uses `Color.black`.
- All TMP `fontSize == 16` (or 32/48 for the rare big title).
- Backgrounds use a project sprite (`woodframe.png` etc.), not a flat
  Image color.
- Reused widgets pull from existing project sprites where possible
  (`check.png` for toggle checkmarks, `x.png` for close glyphs, etc.).
  Anything still on Unity defaults is **flagged** to the user as
  "needs custom asset" — not silently shipped.

### Saving
- **Don't** call `manage_scene save`. Ever, unless the user explicitly
  asks. The user saves when they're satisfied with the result.

### Visual review
- For cosmetic / layout changes, **ask the user for a screenshot** before
  declaring done. Structural assertions catch wiring bugs; only a
  screenshot catches rendering issues — fuzzy text, overlapping rows,
  off-by-pixel alignment, wrong colors against the panel background.

## Known limitations

- `manage_ui` is for **UI Toolkit (UXML)**, not uGUI. The project uses uGUI
  (Canvas + Image + TMP); use `manage_gameobject` + `manage_components` +
  `execute_code` instead.
- `manage_camera screenshot` may not capture overlay-rendered Canvas content
  the way the user sees it; for visual review, ask the user for a screenshot.
