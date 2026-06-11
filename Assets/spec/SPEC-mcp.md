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
3. Saving the scene (`manage_scene save`) and switching scenes is fine at your
   discretion — do it when a task needs it (e.g. swapping to the scene that owns
   the objects you must edit). GitHub history is the restore path and is kept
   reasonably current. Still avoid the **risky** direct YAML `.unity`/`.prefab`
   *file* writes below — those are a different operation from an in-editor save.
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
- **MCP commands are gated on Unity's editor loop, which stalls when Unity is
  unfocused.** Every call (even read-only) runs on the main thread via
  `EditorApplication.update`, throttled to a crawl when the editor window isn't
  focused. A call that hangs or returns `Command processing timed out after
  30000 ms` usually means Unity is starved, not that anything failed — don't
  hammer retries; ask the user to focus Unity (one click flushes the queued
  command). For long/unattended runs the loop only ticks if **Unity** (not
  VSCode) is foreground; Edit/Write and `refresh_unity` still work with Unity
  focused, so leaving Unity focused is the correct unattended setup.
- **A recompile tears down the bridge; auto-resume can fail for good.** Script
  compilation triggers a domain reload that kills the bridge, which retries on a
  ~49s backoff (0,1,3,5,10,30s) — but if Unity stays unfocused through that
  window the resume exhausts and the bridge stays down until a focus + recompile
  (user must restart MCP). So: batch script edits, fire one `refresh_unity
  compile=request`, then poll `editor_state` with backoff (don't spray MCP calls
  into the reload gap), then `read_console` before relying on new types.
  (Play-mode toggles are safe — this project disables Reload Domain, so
  entering/exiting Play doesn't reload.)
- **Cached instance IDs go stale across a domain reload.** Numeric `GetInstanceID()` values
  captured before a recompile may resolve to `null` (or the wrong object) afterward. In
  multi-step flows that span a compile, re-resolve objects by name/component each call
  (`Resources.FindObjectsOfTypeAll(type)` filtered to `scene.IsValid()`, then `Find("child")`)
  rather than reusing IDs from an earlier call.
- **CodeDom string interp** (`$"..."`) works fine, but multiline interpolations
  with `:format` specifiers can confuse it — prefer `string.Format` or
  `.ToString("0.00")` for safety.
- **`UnityEditor.Events.UnityEventTools.AddPersistentListener`** is how you
  wire onClick to a method on a specific component instance from code.
  Pair with `RemovePersistentListener` when re-wiring after destroying the
  target.
- **Multi-Unity instances**: if MCP errors with "multiple connected, no
  active instance set", call `set_active_instance` once with `Name@hash`.
- **Slider handle defaults need fixing for custom sprites.** A
  `DefaultControls.CreateSlider` handle stretches vertically and overshoots
  the track ends at min/max. The non-obvious part: the Slider *drives* the
  handle's anchors (resets them to stretch every `UpdateVisuals`), so you
  can't pin the handle's height via its own anchor — set the height on the
  **parent** Handle Slide Area and let the handle stretch to it, and inset
  the slide area by `handleWidth/2` to stop edge overshoot. The volume
  sliders in `OptionsPanel` are a working reference — duplicate that
  configuration rather than re-deriving the anchor math.

## UI style conventions

These are the project's existing conventions. **Match them.** Anything new
should look at home next to ResearchPanel / TradingPanel / RecipePanel.

### Canvas / pixel scale

- Reference resolution: **960 × 540** (16:9 half-1080p)
- Canvas Scaler: **Constant Pixel Size**, **160 reference pixels per unit**. The
  user-facing UI scale is the scaler's `scaleFactor`, driven by a settings slider
  (`SettingsManager.uiScale`) — see SPEC-ui.md "UI scaling & text crispness".
- Pixel Perfect Camera: assets at **16 PPU** (1 art-pixel = 10 canvas-pixels
  at 1× scale). World sprites use Point filtering; UI sprites (ItemIcons +
  UIChrome atlases, Researches/Skills icons) use **Bilinear** because the UI
  scale factor is non-integer — see SPEC-rendering.md "Sprite atlasing".
- Implication: prefer integer sizes that play well with this scale. Multiples
  of 8 are usually safe; 16 is the cleanest "art tile" unit.

### Font

- **Default font asset: `Figtree SDF` @11** ("Smooth"; `TMP_Settings.defaultFontAsset`).
  m5x7 SDF @16 ("Pixel") is the player-selectable alternate. Both are SDF (not bitmap) so
  they stay crisp at non-integer UI scales; `UITextRuntimeStyle` + material `_Sharpness=1.0`
  keep them sharp and uniform. See SPEC-ui.md "UI scaling & text crispness" + "In-game font
  switcher" before touching font/scale/crispness.
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
Watch out for Slider handles specifically: the Slider drives the handle's
anchors, so a custom-sprite handle needs the fixed-height-on-Slide-Area trick
— see "Slider handle defaults need fixing for custom sprites" under Common
gotchas.

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
- **Never center-align TMP text** (`alignment` of Center / Midline). Centering
  lands the m5x7 pixel font on half-pixels and renders it blurry. Always use
  edge-pinned alignment — `Bottom`/`Top` + `Left`/`Right` (e.g. `BottomLeft`) —
  so glyphs sit on integer pixels. Inset the text RectTransform for padding
  instead of relying on centring.
- Backgrounds use a project sprite (`woodframe.png` etc.), not a flat
  Image color.
- Reused widgets pull from existing project sprites where possible
  (`check.png` for toggle checkmarks, `x.png` for close glyphs, etc.).
  Anything still on Unity defaults is **flagged** to the user as
  "needs custom asset" — not silently shipped.

### Saving
- Saving (`manage_scene save`) and switching scenes is fine at your discretion
  when the task needs it — GitHub history is the restore path. No need to ask
  first. (Still don't do risky direct YAML `.unity`/`.prefab` file writes.)

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
