# Shonei — Rendering & Lighting

## Rendering & Layers

### Sorting orders (authoritative — keep this up to date)

| sortingOrder | What |
|---|---|
| -10 | Background tile (`BackgroundTile`) |
| 0 | Tiles |
| 1 | Roads (depth-3 structures) |
| 5 | Power shafts (depth-4 structures) — render behind buildings so shafts read as wall-mounted plumbing |
| parent − 1 | Power port stubs (`PortStubVisuals` child SR, one below the parent building). Also: flywheel wheel — rendered behind the housing so the spokes peek through. |
| 10 | Buildings (depth-0 structures) |
| parent + 1 | Rotating wheel children sorted in front of the base (`RotatingPart` child SR — windmill blades). Per-building: the building decides whether its wheel sorts in front or behind by setting `wsr.sortingOrder` relative to its own `sr.sortingOrder` (windmill = +1, flywheel = −1). |
| 11 | Floor items resting on a building's solid top (computed by `Inventory.ComputeFloorSortingOrder`) |
| 15 | Platforms (depth-1 structures); also clock hand |
| 16 | Floor items resting on a platform's solid top (computed) |
| 20 | Water overlay sprite (`WaterController`) |
| 30 | Items in storage/inventory display |
| 40 | Foreground structures (depth-2: stairs, ladders) |
| 48 | Animal tail (paper-doll part) |
| 49 | Animal back foot (paper-doll part) |
| 50 | Animal body (paper-doll part) |
| 51 | Animal front foot (paper-doll part) |
| 52 | Animal arm (paper-doll part) |
| 55–57 | Clothing overlays (per-part children: body 55, foot 56, arm 57) |
| 60 | Plants |
| 64 | Light-source buildings (torch, fireplace) — per-type override via `StructType.sortingOrder`, sits above animals/plants so `LightSource` (auto-detect) front-lights them |
| 65 | Falling items (mid-air animation) |
| 70 | Floor items resting on solid dirt (or fallback when no surface is detected below) |
| 100 | Blueprints |
| 101 | Blueprint frame overlay (unlit, sliced) |
| 200 | Build preview (mouse cursor ghost) |

**Floor-item sort is surface-aware.** `Inventory` (Floor type) picks its sortingOrder based on the tile directly below at (x, y−1): platform-with-`solidTop` → 16, building-with-`solidTop` → 11, anything else → 70. The pile re-sorts whenever a structure is placed/destroyed under it (via `Structure` constructor + `Destroy`) or the supporting tile's type changes (`WorldController.OnTileTypeChanged`). Helpers: `Inventory.ComputeFloorSortingOrder()`, `Inventory.RefreshFloorSortingOrder()`, and the static `Inventory.RefreshFloorAt(x, y)`.

### Structure depth layers

Structures render in five depth layers per tile. Each tile holds `Structure[] structs` and `Blueprint[] blueprints`, both indexed by depth int (size = `Tile.NumDepths`):

| Depth | `structs[d]` | Contents | Sprite position | sortingOrder |
|-------|-------------|----------|----------------|-------------|
| 0 | building layer | Buildings, plants (`Building`/`Plant`) | `(x, y)` | 10 |
| 1 | platform layer | Platforms | `(x, y)` | 15 |
| 2 | foreground layer | Stairs, ladders, torches | `(x, y)` | 40 |
| 3 | road layer | Roads | `(x, y−1/8)` — sits on tile surface | 1 |
| 4 | shaft layer | Power shafts | `(x, y)` | 5 |

Slot index ≠ visual layering. Power shafts live in slot 4 (the highest array index) but render at sortingOrder 5 — *behind* buildings/platforms/foreground but in front of roads. The dedicated slot lets shafts coexist on the same tile as a building, ladder, road, etc.

Depth-based sortingOrder is the default; individual `StructType`s can override via the JSON `sortingOrder` field (e.g. torch=64, fireplace=64). Plant overrides to 60 in its constructor.

`tile.building` is a convenience property: `structs[0] as Building` (Plant extends Building, so both are accessible through it). Multiple layers can coexist on the same tile. `GetBlueprintAt(int depth)` / `SetBlueprintAt(int depth, Blueprint bp)` directly index into `blueprints[]`.

### Blueprint visuals

Each `Blueprint` renders a main sprite (order 100, lit, half-alpha ghost) plus a child **frame overlay** GameObject (order 101, **Unlit** layer, sliced to the footprint). The frame sprite swaps between `Sprites/Misc/blueprintframe` (blue — construct/supply) and `Sprites/Misc/bpdeconstructframe` (red — deconstruct). Frame alpha drops to `0.5` when `disabled || IsSuspended()`. The unlit layer keeps frames visible at night without participating in the lighting pipeline (same pattern as Plant's harvest overlay).

Deconstruct blueprints **hide their own main sprite** and instead apply a multiplicative tint (`DeconstructStructureTint`, currently `(1, 0.5, 0.5)`) to the underlying structure's `sr.color`. Because we only touch `sr.color` and never `sr.sprite`, live sprite changes (plant growth stages, harvest cycles, variant swaps) render correctly through the tint. `Blueprint.Destroy()` restores the structure to `Color.white` on cancel (skipped during `WorldController.isClearing` since structures are being torn down anyway).

---

## Lighting

Custom `ScriptableRendererFeature` pipeline — no URP Light2Ds used. Final result is Multiply-blitted onto the scene.

### Render pipeline (per frame)

1. **NormalsCapturePass** (`BeforeRenderingTransparents`) — draws sprites with `Hidden/NormalsCapture` override into `_CapturedNormalsRT` (format: **ARGB32** — must have alpha; camera's default HDR format has none). **Channel packing:**
   - `R, G` — world-space normal.x, normal.y packed 0–1. Normal.z is **not stored**; it's reconstructed in light shaders as `z = −sqrt(1 − x² − y²)` (assumes camera-facing sprite normals, which all project sprites satisfy).
   - `B` — **receiver sort bucket** (`sortingOrder / 255`), smuggled in per-sprite via `MaterialPropertyBlock` by `LightReceiverUtil.SetSortBucket`. `LightCircle.shader` samples this per-pixel and branches on the sign of `sortDelta = receiverBucket − lightBucket`. **In-front** receivers (`sortDelta > 0`) use `effectiveHeight = −lightHeight` and zero ambient floor, so forward-facing interior normals go dark (their z-dot with toLight flips sign) while edge normals pointing sideways toward the light's XY still get lit — giving a clean silhouette block without killing rim lighting. **Behind** receivers (`sortDelta ≤ 0`) ramp `effectiveHeight` from `+lightHeight` (at delta=0) toward `+lightHeight × behindFarHeightFactor` as the receiver sorts further behind (`smoothstep` over `sortRampRange`), keeping the full `ambientNormal` floor. Default `behindFarHeightFactor = 1.0` → identity (matches pre-sort-aware lighting exactly). The RT uses `FilterMode.Point`, so the in-front/behind boundary is always sprite-aligned — the effective-height sign flip never appears inside a single sprite.
   - `A` — lighting tier + edge depth:
     - `0.80–1.0` — solid tile. The range encodes **edge depth** for underground darkening: `1.0` = at exposed tile surface (fully lit), `0.80` = deep interior (darkened to `deepFloor`). Extracted as `saturate((alpha - 0.80) / 0.20)` in `LightComposite`. (Previously also used for shadow casting — ray march is commented out in `LightSun.shader`.)
     - `0.5` — lit-only (full light)
     - `0.3` — directional-only (sun + ambient only; `LightCircle` skips torch for these pixels)
     - `0.0` — no sprite (flat-normal fallback in light shaders)

   **Tile border clipping**: Tiles use pre-baked 20×20 sprites (from `TileSpriteCache`) whose alpha already encodes the border shape. NormalsCapture clips on `_MainTex` alpha for both tiles and non-tiles — no per-pixel atlas lookup needed.

   Draw calls in order (later overwrites earlier where they overlap, `Blend Off`):
   - Background (`backgroundLayer`): `NormalsCaptureBackground` override, pass 1 (alpha = 0.5, lit-only). Clips transparent top pixels so they read as sky. Drawn earliest so tiles/sprites overwrite.
   - Pass 2 (`directionalOnlyLayers`): alpha = 0.3 — drawn next so shadow-caster pass can't overwrite
   - Pass 1 (`litLayers & ~shadowCasterLayers & ~directionalOnlyLayers`): alpha = 0.5
   - Pass 0 (`litLayers & shadowCasterLayers & ~directionalOnlyLayers`): alpha = `lerp(0.80, 1.0, _NormalMap.a)` where `_NormalMap.a` carries edge-distance falloff baked by `TileSpriteCache`

2. **LightPass** (`AfterRenderingTransparents`) — `ConfigureTarget(LightRTId)` in `OnCameraSetup` binds the temp RT (required for the clear and for `cmd.Blit` to target it correctly across all cameras). Ambient light is split into two parts:
   - **Deep ambient** (constant): `LightFeature.deepAmbientColor` — clears the light RT. Present everywhere, even deep underground. Not affected by time of day. Tunable on `LightFeature` inspector.
   - **Sky light** (time-varying, distance falloff): `SunController.GetAmbientColor()` — blitted via `LightAmbientFill.shader` which samples `_SkyExposureTex` (set by `SkyExposure`). Falls off with distance from sky-exposed tiles. Changes with day/night cycle.
   
   Then draws:
   - Point lights (torches, etc.): `cmd.DrawMesh` per-light quad scaled to `outerRadius×2`, screen blend (`BlendOp Add, Blend One OneMinusSrcColor`), radial falloff × NdotL. **Skips pixels where normals RT alpha is 0–0.4** (directional-only tier).
   - Sun (directional): `cmd.Blit(null, LightRTId, sunMat)`, additive blend (`BlendOp Add, Blend One One`), NdotL with `_SunDir`. Shadow ray march is **disabled** (commented out in `LightSun.shader` for performance). **Must use `cmd.Blit`, not `cmd.DrawMesh`** — DrawMesh silently fails to write to the temp RT for cameras without PixelPerfectCamera (e.g. SkyCamera). Blit handles its own fullscreen geometry and RT binding internally, bypassing the issue.

3. **Composite** — `cmd.Blit(lightRT, scene, LightComposite)` multiplies scene by light map (`Blend DstColor Zero`). Empty sky/background pixels (normals RT alpha < 0.25) use a precomputed `_SkyLightColor` (sun + time-of-day ambient, no sky-exposure modulation, no point lights), blended via `skyLightBlend` (tunable on `LightFeature` inspector, default 1.0). Those pixels' base color comes from `SkyCamera.backgroundColor`. **Underground darkening**: solid-tile pixels (alpha > 0.75) are additionally scaled by `lerp(deepFloor, 1.0, edgeDepth)` — deep tile interiors get dimmed to `deepFloor` brightness (tunable on `LightFeature` inspector, default 0.2). After deepFloor dimming, a `max(light, deepAmbient)` clamp ensures tile interiors never go below the universal deep ambient.

**LightFeature skips cameras** where `cullingMask == 0` or where `(cullingMask & (litLayers | directionalOnlyLayers | waterLayer | backgroundLayer)) == 0` — i.e. cameras that see no sprites participating in the normals RT. The `UnlitOverlayCamera` (see Sky/background below) hits this check because the Unlit layer is excluded from all four masks.

### Sky / background

Three cameras render in depth order:

| Camera | Depth | Clear Flags | Culling Mask | Notes |
|--------|-------|-------------|--------------|-------|
| `SkyCamera` | 0 | Solid Color | Sky layer | `backgroundColor` set to `baseSkyColor × GetAmbientColor()` each frame — sky darkens at night |
| Main Camera | 1 | Don't Clear | Everything except Unlit | PixelPerfectCamera; lighting composite applied here |
| `UnlitOverlayCamera` | 2 | Don't Clear | Unlit only | Renders after composite — sprites on the **Unlit** layer appear at full brightness, unaffected by lighting. Has `MatchCameraZoom` component to sync `assetsPPU` from Main Camera. LightFeature pipeline is skipped for this camera entirely. |

**Unlit layer pattern**: any sprite that should always appear at full brightness (tile highlights, selection overlays, debug markers) goes on the `Unlit` layer. Keep it excluded from `litLayers`, `shadowCasterLayers`, and `directionalOnlyLayers` in the LightFeature Inspector. **Also assign a sprite-unlit material** (`Sprite-Unlit-Default`) — the Unity default sprite material is lit, and a lit shader on the UnlitOverlayCamera pass samples no lights → renders black. For runtime-created overlays, either instantiate a prefab that carries the material (preferred — see `Plant.CreateHarvestOverlay` / `BuildIndicator`) or cache a material via `Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")`.

### Sky exposure (`SkyExposure.cs`)

`Assets/Lighting/SkyExposure.cs` — scene singleton (under Lighting), initialized by `WorldController.GenerateDefault()` and `SaveSystem.Load()`.

**Two-part ambient model**: ambient light = deep ambient (constant) + sky light (time-varying, distance falloff).
- **Deep ambient** = `LightFeature.deepAmbientColor` — constant color, clears the light RT. Always present, even deep underground. Not affected by day/night cycle.
- **Sky light** = `GetAmbientColor()` — added via `LightAmbientFill.shader`, modulated by `_SkyExposureTex`. Emitted from sky-exposed tiles (`!hasBackground`), falls off with Chebyshev distance into surrounding material. Falloff depth = `LightFeature.penetrationDepth` (shared with the sub-tile edge-depth baked into tile normal maps by `TileSpriteCache`). Changes with day/night cycle. Sun is also modulated by `_SkyExposureTex` in `LightSun.shader`.

**Sky exposure texture** (`_SkyExposureTex`): R8, nx×ny, **Bilinear filtered** for smooth sub-tile gradients. Set as global shader texture. Built via multi-source BFS flood fill from sky-exposed tiles — distance mapped through smoothstep to 0–255. Falloff range is `penetrationDepth + 1` so that `penetrationDepth = 1` means "1 tile of visible reach beyond the source" (the +1 offset prevents the first neighbor from getting zero exposure). Rebuilt when any tile's background wall changes.

**Edge-depth blending** (`LightComposite`): shadow-caster pixels blend toward `deepAmbientColor` based on edge depth: `lerp(deepAmbient, light, edgeDepth)`. Deep tile interiors are exactly `deepAmbientColor` everywhere regardless of sky/sun contribution.

**Sky camera ambient**: `LightPass` detects `SkyCamera` and clears the light RT to **full ambient** (skipping the `LightAmbientFill` blit). This prevents sky light spatial falloff from affecting clouds on the Sky layer. Clouds still receive sun via the directional light pass.

### Background tile (`BackgroundTile.cs`)

`Assets/Controller/BackgroundTile.cs` — scene singleton (under Lighting), initialized by `WorldController.GenerateDefault()` and `SaveSystem.Load()`.

**Per-tile background**: each `Tile` has a `hasBackground` bool (with `cbBackgroundChanged` callback). During world generation, tiles at y ≤ 43 are given a background. The flag is saved/loaded in `TileSaveData` (as `hasBackgroundWall` for backward compat).

**Background sprite**: a world-spanning sprite on the **Background layer** at **sorting order −10** (behind tiles at 0). Uses `BackgroundTile.shader` (tagged `Universal2D` for normals capture), masked by a low-res RGBA32 texture (nx × ny, 1 pixel per tile). The mask encodes two things: **alpha** = background present (opaque/transparent), **green** = top-row flag (G=255 if the tile above has no background, G=0 otherwise). The shader samples one of two tileable 16×16 textures based on the green channel: `_WallTex` (`undergroundwall`) for interior tiles, `_WallTopTex` (`undergroundwalltop`) for top-row tiles. Both tile at 1 repetition per world unit via world-space UVs. Participates in normal lighting (sun, torches, sky light) via dedicated `NormalsCaptureBackground` override in `NormalsCapturePass` — clips transparent top pixels so they read as sky in the normals RT. Wall textures are set as globals (`_BackgroundTex`, `_BackgroundTopTex`) by `BackgroundTile.cs` for the override shader to access. Rebuilt on background or tile type change via dirty flag.

### Key files

All lighting C# scripts and shaders live in `Assets/Lighting/`.

| File | Role |
|------|------|
| `LightFeature.cs` | `ScriptableRendererFeature` containing `NormalsCapturePass` + `LightPass`. Inspector tunables: `ambientNormal`, `lightPenetrationDepth`, `deepAmbientColor`, `skyLightBlend`, `sortRampRange`, `behindFarHeightFactor`, layer masks. |
| `LightSource.cs` | Component: `lightColor`, `intensity`, `outerRadius`, `innerRadius`, `lightHeight`, `isDirectional`, `sortOrderOverride`. Registers itself in a static list read by `LightPass`. `sunModulated` (default false): when true, `SunController` overrides intensity by time of day (torches/fireplaces). Non-modulated lights keep their set intensity always. `sortOrderOverride = -1` (default) auto-reads from a SpriteRenderer on the GameObject or its parents — this value becomes `_LightSortBucket` in the shader for the sort-aware effective-height ramp. |
| `LightReceiver.cs` | `LightReceiverUtil.SetSortBucket(SpriteRenderer)` writes `_SortBucket = sortingOrder/255` onto the renderer's `MaterialPropertyBlock`. Called at every code site that sets `sortingOrder` on a lit sprite. The `LightReceiver` MonoBehaviour variant is for prefabs with editor-authored sortingOrder (e.g. animal paper-doll parts) — attach one component to a root and it walks all child SpriteRenderers in `Start()`. |
| `SunController.cs` | Orbiting sun, sky color, `GetAmbientColor()`, `GetSunDirection()`. Sun child has a `LightSource (isDirectional=true)`. Inspector tunables: orbit, twilight timing, sky/sun/ambient color gradients, `sunIntensityNoon`, `ambientBrightnessMin`/`ambientBrightnessRange`. |
| `NormalsCapture.shader` | Tangent→world normal transform for 2D sprites. Vertex shader sends per-renderer world-space tangent/bitangent (sprite's local +X/+Y in world, derived from the transform); fragment forms `wn = tn.x·worldT + tn.y·worldB + tn.z·(0,0,−1)`. Reduces to `(x, y, −z)` for unrotated sprites; rotating sprites (flywheel wheel, windmill blades) get correctly rotated normals so the lit side stays in world space rather than spinning with the texture. Clips on `_MainTex` alpha. |
| `TileSprite.shader` | Simple tile sprite shader: samples `_MainTex` (pre-baked 20×20 from `TileSpriteCache`), clips transparent pixels. Assigned to tile SpriteRenderers by WorldController. |
| `CrackedSprite.shader` (`Assets/Resources/Shaders/`) | Broken-structure overlay — composites a tileable world-space crack texture on top of `_MainTex`, alpha-masked by base sprite. URP 2D-tagged (both `Universal2D` and `UniversalForward` passes) so broken renderers stay in the NormalsCapture filter. Swapped in via `Structure.RefreshTint()`. See SPEC-systems.md §Maintenance System / Visual. |
| `TileSpriteCache.cs` | Bakes 20×20 tile sprites **and matching normal maps** at load time from 32×32 border atlases. 16 cardinal-mask sprites per artist texture. A tile type may ship multiple variant textures named `<tileName>`, `<tileName>2`, `<tileName>3`, … (either atlases in `Sheets/` or flat sprites in `Tiles/`); each world tile picks one deterministically from its (x, y), stable across re-renders and loads. PPU=16 → sprites natively span 1.25 units. Normal maps are derived from each baked variant's own alpha — RGB is outward-facing at every alpha boundary, A is a distance-transform edge-depth used by `LightComposite` for underground darkening. Exposes `FlatNormalMap` for non-solid tiles. |
| `LightCircle.shader` | Point light pass: radial falloff × NdotL. |
| `LightSun.shader` | Directional sun pass: fullscreen NdotL × sky exposure. Shadow ray march disabled (commented out for performance). |
| `LightAmbientFill.shader` | Fullscreen pass: writes `skyLight × exposure` per pixel. Max blend onto deep-ambient-cleared RT. |
| `LightComposite.shader` | Multiply blit onto scene + edge-depth blending toward deepAmbient for deep tile interiors. |
| `SkyExposure.hlsl` | Shared HLSL include: declares `_CamWorldBounds`, `_GridSize`, `_SkyExposureTex` and provides `SampleSkyExposure(screenUV)`. Used by LightAmbientFill and LightSun. |
| `BackgroundTile.shader` | Tiles `_WallTex` or `_WallTopTex` (selected by mask green channel) at world-space UVs, masked by `_MainTex`. Tagged `Universal2D` for normals capture. |
| `NormalsCaptureBgTile.shader` | Normals capture override for background (shader name is `Hidden/NormalsCaptureBackground` — what `LightFeature.cs` loads by). Samples `_BackgroundTex`/`_BackgroundTopTex` (globals set by `BackgroundTile.cs`) and clips transparent pixels — fixes jagged top-edge lighting. |
| `SkyExposure.cs` | Sky exposure texture (R8, per-tile, BFS distance falloff from sky-exposed tiles). Scene singleton (under Lighting). |

`Assets/Editor/SpriteNormalMapGenerator.cs` — sprite normal map batch tool (must stay in `Editor/`).

### Global shader properties

All globals are set via `cmd.SetGlobal*()` in C#. **Rule**: per-camera globals go in the dedicated "Per-camera globals" block at the top of `LightPass.Execute()`, before any camera-specific branching. This prevents cameras from inheriting stale values from a previous camera's render.

| Property | Type | Set by | Frequency | Read by |
|----------|------|--------|-----------|---------|
| `_CamWorldBounds` | Vector4 | LightPass §1 | Per-camera | LightAmbientFill, LightSun (via `SkyExposure.hlsl`) |
| `_WorldToUV` | Vector2 | LightPass §1 | Per-camera | LightSun |
| `_AmbientNormal` | float | LightPass §1 | Per-camera | LightSun, LightCircle |
| `_DeepAmbient` | Color | LightPass §1 | Per-camera | LightComposite |
| `_AmbientColor` | Color | LightPass §2 | Per-camera (Main only) | LightAmbientFill |
| `_SunColor` | Color | LightPass §4 | Per-light | LightSun |
| `_SunIntensity` | float | LightPass §4 | Per-light | LightSun |
| `_SunDir` | Vector3 | LightPass §4 | Per-light | LightSun |
| `_SunHeight` | float | LightPass §4 | Per-light | LightSun |
| `_SkyExposureTex` | Texture2D | SkyExposure.cs | On dirty | LightAmbientFill, LightSun (via `SkyExposure.hlsl`) |
| `_GridSize` | Vector4 | SkyExposure.cs | On dirty | LightAmbientFill, LightSun (via `SkyExposure.hlsl`) |
| `_WaterSurfaceTex` | Texture2D | WaterController.cs | Every 0.2s | NormalsCaptureWater |
| `_BackgroundTex` | Texture2D | BackgroundTile.cs | Once (init) | NormalsCaptureBackground |
| `_BackgroundTopTex` | Texture2D | BackgroundTile.cs | Once (init) | NormalsCaptureBackground |

**§1–§5** refer to the numbered sections inside `LightPass.Execute()`. "On dirty" means the property is only re-set when the underlying data changes (e.g. a background tile is placed/removed), not every frame.

---

## Animal Paper-Doll System

Animals use a paper-doll (multi-sprite) approach: each body part is a separate child GameObject with its own `SpriteRenderer`, animated via transform keyframes (rotation, position) rather than sprite-swapping.

### Prefab hierarchy
```
Animal (root — Animator, Animal.cs, BoxCollider2D, no SpriteRenderer)
├─ Tail           (order 48)
├─ BackFoot       (order 49)
├─ Body           (order 50)  ← Animal.sr references this renderer
│  └─ ClothingBody  (order 55)
├─ FrontFoot      (order 51)
└─ Arm            (order 52)
```

### Facing direction
Flip is done via `transform.localScale.x = -1` on the root, which mirrors all children. No per-renderer `flipX`.

### Per-part clothing
`AnimationController.clothingParts` is a serialized array of `PartClothing` entries (partName + renderer reference). Each entry loads its sprite from `Resources/Sprites/Animals/Clothing/{item}/{partName}.png`. Missing sprites are handled gracefully (renderer stays disabled). Clothing renderers are children of their body part, so they inherit transforms automatically.

### Sprite assets
Part sprites in `Assets/Resources/Sprites/Animals/`: `mouse_body.png`, `mouse_tail.png`, `mouse_foot.png`, `mouse_arm.png` (+ `_n.png` normal maps). Pivots set in Sprite Editor per part (feet: top, arm: top, tail: base, body: center).

### Animation states & pose overrides

The Animator (`AnimControllerMouse.controller`) is driven by two int parameters set each frame in `AnimationController.UpdateState()`:

| Parameter | Source | Values |
|-----------|--------|--------|
| `state` | `animal.state` (high-level activity) | 0 = Idle, 1 = Moving, 2 = Eeping |
| `pose` | `animal.task?.currentObjective?.PoseOverride` → `PoseToInt` | 0 = none (state drives), 1 = sit |

Each state/pose corresponds to a single `.anim` clip. Stationary poses are fine as 2-frame static clips (see `mouseEep.anim` as the reference — just holds per-part transforms). Pose wins over state: whenever `pose != 0` the animal is in that pose regardless of walking/idle/eep.

**Animator wiring** for a new pose state:
- Add state `mouse<Pose>` with the pose's `.anim` as Motion.
- Transition **Any State → mouse<Pose>**: `pose Equals <N>`, Has Exit Time off, Transition Duration 0, **Can Transition To Self unchecked** (otherwise it re-enters every frame and freezes at frame 0).
- Transition **mouse<Pose> → mouseIdle**: `pose Equals 0`, Has Exit Time off, Transition Duration 0. From `mouseIdle` the existing `state`-based transitions take over.

**How a pose gets triggered.** Pose is data-driven: `StructType.leisurePose` in JSON names the pose, `LeisureObjective.PoseOverride` reads it off the seated building (for `LeisureTask.building` and `ReadBookTask.seatBuilding`). `WorkObjective.PoseOverride` reads `StructType.workPose` off the CraftTask's workplace building (mirrors leisurePose). Since the override is a pure getter derived from the current objective, it self-clears on objective transition — no explicit set/reset plumbing.

**Special case `"walk"`**: `AnimationController.UpdateState` routes a pose of `"walk"` to `state = 1` (Moving) and `pose = 0`, reusing the existing walk clip instead of needing a duplicate animator state. Used by the wheel runner so the mouse cycles its legs while producing power. Authoring a new walk-derived pose isn't needed — just JSON `"workPose": "walk"`.

**Adding a new pose**:
1. Author `.anim` clip in Unity (copy `mouseEep.anim` as a starting point for a stationary pose).
2. Add an Animator state + transitions per the wiring above, using the next free `pose` int.
3. Add a case to `AnimationController.PoseToInt` — e.g. `case "read": return 2;`. Unknown strings LogError and fall through to 0.
4. Pick where the pose name originates:
   - **Leisure-tied** (cushion, reading nook): `"leisurePose": "<name>"` in `buildingsDb.json`. No code changes.
   - **Task-tied** (crafting, studying): override `PoseOverride` on the relevant Objective subclass, or add a parallel field (`workPose`) and a matching getter on the Objective that runs that activity.

---

## Water Rendering

See `SPEC-systems.md` for the simulation. The renderer is a separate GPU shader pipeline.

**Files**: `Assets/Lighting/Water.shader`, `Assets/Controller/WaterController.cs`

**Surface mask** (`TextureFormat.R8`, nx×16 × ny×16 pixels): one byte per game pixel, rebuilt by `WaterController.UpdateSurfaceMask()` every 0.2 s (sim tick). Values: `0`=transparent, `127`=interior water, `255`=surface pixel. A pixel is "surface" if any of its 8 orthogonal+diagonal neighbours is open air (non-solid, no water). Water touching solid walls is NOT flagged as surface.

**Shader** (`Water/WaterSurface`): one texture sample per fragment, three branches:
- `mask < 0.25` → `discard` (transparent)
- `mask > 0.75` → `_SurfaceColor` (white highlight)
- else → `lerp(_WaterColorDark, _WaterColorLight, sin(_Time.y…) * 0.4)` (shimmer)

**Layer**: the `WaterSprite` GameObject must be on the **`Water`** Unity layer, excluded from:
- `LightFeature` `litLayers` / `shadowCasterLayers` / `directionalOnlyLayers` — water is NOT handled by the standard NormalsCapture path
- `SkyCamera` culling mask (otherwise water appears in the sky)

**Lighting**: water is lit via a dedicated path. `LightFeature` has a `waterLayer` field (set to `Water` in the Inspector) which triggers a separate `DrawRenderers` call in `NormalsCapturePass` using `Hidden/NormalsCaptureWater` (pass 1, lit-only, alpha=0.5). That shader samples the global `_WaterSurfaceTex` (set each tick by `WaterController.UpdateSurfaceMask()`) for transparency, discarding pixels with no water. Outputs flat forward normals. This means water darkens at night and receives ambient light, but torch NdotL is minimal (flat normal faces away from scene).

**sortingOrder**: `20` — above buildings (10), platforms (15), and floor items resting on either (11/16) so decorative water zones render on top of building sprites and any items piled there.

### Decorative water zones

Buildings can have pixel regions that render as water shimmer without participating in the fluid simulation. Used for two distinct cases:
- **Fountain basins** (binary): all zone pixels render when a `Reservoir` has fuel.
- **Tanks / liquid storage** (fill-level): only the bottom fraction of zone pixels renders, scaled continuously to the stored liquid's quantity vs total capacity. The tank branch scans `storage.itemStacks` for the first non-empty `isLiquid` stack, so any liquid — water, soymilk, future liquids — renders the same way. The top row of rendered pixels gets the surface highlight (`255`) so the water shimmers white at the surface, just like a pond.

**Companion mask sprite (`{name}_w.png`)**: the zone is defined by a companion mask texture beside the main building sprite — e.g. `Sprites/Buildings/tank.png` + `Sprites/Buildings/tank_w.png`. The mask must match the main sprite's dimensions exactly. Any pixel with alpha ≥ 128 is part of the water zone; the pixel's colour is irrelevant. Analogous to the `_f.png` emission/fire mask pattern used by [SpriteNormalMapGenerator](../Editor/SpriteNormalMapGenerator.cs) — separates "where water goes" from the visible art.

**Requirements**: both the main sprite and its `_w.png` companion must have **Read/Write Enabled** in their Unity Import Settings. [BuildingSpritePostprocessor.cs](../Editor/BuildingSpritePostprocessor.cs) auto-enables this for all textures under `Resources/Sprites/Buildings/` on import.

**How it works**: `WaterController.ScanWaterPixels()` is called from the `Structure` constructor — it loads `Resources/Sprites/Buildings/{stem}_w` (where `stem` is the main sprite's texture name) and collects opaque pixel offsets. If no companion exists, returns null and the building has no water zone. `StructController.Place()` then calls `WaterController.RegisterDecorativeWater()`, which converts offsets to world-pixel coordinates (applying `mirrored` flipX) and caches the local Y range. Each `UpdateSurfaceMask()` tick, registered zones are overlaid into `_surfaceBytes`.

Per-zone gating (in order — first match wins):
1. `Building.reservoir != null` — fountain-style: render all pixels as `127` iff `reservoir.HasFuel()`.
2. `structType.liquidStorage && storage != null` — tank-style: find the first non-empty `isLiquid` stack, compute `fillRows = round(liquidFen / capacityFen × totalRows)`, render only pixels with `localY < localMinY + fillRows`. The pixel at `localY == localMinY + fillRows − 1` is written as `255` (surface); below is `127`.
3. No gating — render all pixels as `127`.

**Per-liquid tint**: tanks additionally stamp their stored liquid's `liquidColor` (parsed from `liquidColorHex` in `itemsDb.json`) into a tile-resolution `_TintTex` (RGBA32, `nx × ny`, Point-filtered). In the shader, when `tint.a > 0.5` the interior shimmer uses `tint.rgb` as the light color and `tint.rgb × 0.85` (15% darker) as the dark color — otherwise it falls back to the global `_WaterColorDark/Light`. Natural simulated water tiles, fountains, and liquids without a `liquidColorHex` all take the fallback path. The surface highlight stays global white regardless. Point filtering is required so adjacent tiles with different tints don't bleed across tile borders.

**Interaction with generic storage sprite**: `Inventory.UpdateSprite()` skips rendering the usual `slow/smid/shigh` storage sprite when `isLiquidStorage` is true — the water shader is the sole visual for liquid fill. This is what makes tank water look like a continuous column rather than three snap levels.

---

## Item Sprites

Item sprites live under `Assets/Resources/Sprites/Items/` in two sub-folders:

| Folder | Contents |
|--------|----------|
| `Sheets/` | Source sprite sheets (one per item, 2-col × N-row grid, `CellSize=16`). Kept in source control; not loaded at runtime. |
| `split/` | Split output — one sub-folder per item name (e.g. `split/wood/`), containing individual PNGs named by variant. These are what `Resources.Load` targets at runtime. |

### Split-folder variant names

| File | Usage |
|------|-------|
| `icon.png` | Default display (UI, animal inventory, fallback) |
| `floor.png` | Item dropped on a floor tile |
| `slow/smid/shigh.png` | Storage fill level (low / mid / high) |
| `qlow/qmid/qhigh.png` | Animal carry-stack quantity level |

A `default/` folder inside `split/` holds fallback sprites used when an item has no specific sprite.

### ItemSheetSplitter

`Assets/Editor/ItemSheetSplitter.cs` — editor-only tool. Reads `Sheets/{itemName}.png`, cuts it into individual PNGs, and writes them to `split/{itemName}/{slotName}.png`.

- **Tools → Split All Item Sheets** — processes every sheet in `Sheets/`
- **Right-click sheet → Split Item Sheet** — processes selected sheet(s)

After splitting, normal maps for the new files can be regenerated via **Tools → Generate All Sprite Normal Maps**.

---

## Plant Sprites

Same Sheets/Split pattern as items. Source sheets live in `Assets/Resources/Sprites/Plants/Sheets/`, split output in `Assets/Resources/Sprites/Plants/Split/{plantName}/`. Each sheet is 64×16 (4 columns of 16×16 cells), producing `g0.png`–`g3.png` for growth stages 0–3.

`PlantSheetSplitter.cs` — **Tools → Split All Plant Sheets**. Sprite loading falls back: `g{stage}` → `g0` → `default`.

---

### Normal maps

**Encoding**: tangent space (sprite-local), packed 0–1. Out-of-texture = local +Z. Flat camera-facing pixel = tangent `(0, 0, +1)` → packed `(0.5, 0.5, 1.0)` (= `(128, 128, 255)` byte). `NormalsCapture.shader` transforms this to world space using the renderer's own basis (`worldT`, `worldB` derived from `TransformObjectToWorldDir`), so rotating sprites get correctly rotated normals. For an unrotated, axis-aligned sprite the transform reduces to `(x, y, −z)` in world space. Black = no sprite, shader uses flat fallback. No Y-flip on screen UV (DrawRenderers and the light pass projection both use OpenGL convention, V=0 at bottom).

**Tile normal maps** (`TileSpriteCache.BakeNormalMap`): baked per-variant alongside each 20×20 sprite and driven by the sprite's own alpha + an 8-bit adjacency mask (4 cardinals + 4 diagonals). Sprites are keyed by the 4-bit cardinal mask (16 entries per variant); normal maps are keyed by the full 8-bit mask (256 entries) because diagonal openings change both the RGB bevel and the edge-depth alpha at inside corners. For each pixel position we compute an **effective opacity**: interior pixels use their sprite alpha; the four 2×2 overhang corners (e.g. (0–1, 0–1) = BL) are treated as opaque when the matching diagonal neighbour is solid (it spatially owns that corner) and fall back to sprite alpha otherwise; non-corner overhang pixels (strictly one side of the interior) are opaque when the matching cardinal neighbour is solid and fall back to sprite alpha otherwise; positions outside the 20×20 are treated as transparent (empty air). RGB is a Sobel-combined outward bevel normal: cardinal transparent neighbours contribute a full ±1 outward unit, and the four diagonal transparent neighbours each contribute `±BevelDiagWeight` (default 0.5) to both axes. This widens the 1-pixel bevel into a ~1.5px-wide soft bevel — the directly-adjacent ring keeps its full outward tilt, and the next ring in picks up a gentle diagonal tilt so e.g. inside-corner interior pixels (solid L + solid D + empty BL diagonal) catch grazing light. A dithered grass surface correctly lights the row just below any punched-through top pixel via the cardinal term. Alpha is a Euclidean distance-transform to the nearest effectively-transparent pixel, mapped through a smoothstep over `LightFeature.penetrationDepth * TILE` (16 px at default); it drives `LightComposite`'s fade toward `deepAmbient` for underground darkening. The transparent BL 2×2 overhang at an inside corner also lets the distance transform reach into the interior, adding edge-depth brightening on top of the diagonal bevel. Applied via `MaterialPropertyBlock` on tile `SpriteRenderer`s; non-solid tiles use `TileSpriteCache.FlatNormalMap`.

**Tile border atlases** (source format): 32×32 artist-authored textures in `Assets/Resources/Sprites/Tiles/Sheets/{name}.png`. Layout: main 16×16 at (8,8), top/bottom 16×4 borders at (8,0)/(8,28), left/right 4×16 borders at (0,8)/(28,8), four 4×4 corner pieces at (0,0)/(28,0)/(0,28)/(28,28). Columns 1,6 and rows 1,6 are empty separators. **Not sampled at runtime** — `TileSpriteCache` reads pixel data at load time to bake 16 cardinal-mask variants per tile type as 20×20 Sprites (PPU=16 → 1.25 units). Textures must have Read/Write enabled (`TileSpritePostprocessor` handles this automatically).

**Sprite normal maps** (`SpriteNormalMapGenerator.cs`): editor tool (**Tools → Generate All Sprite Normal Maps**) batch-processes `Assets/Resources/Sprites/`. For each source texture (skipping `_n.png`, `_f.png`, and `_e.png` companions):
1. Generates `{stem}_n.png` — edge pixels get outward normals, interior gets flat forward normal.
2. Imports as `Default` / `Uncompressed` RGBA32 (not NormalMap type — must stay plain packed 0–1).
3. Auto-assigns as `_NormalMap` secondary texture on the source sprite importer.
4. If `{stem}_e.png` exists, auto-assigns it as `_EmissionMap` secondary texture on the source sprite.

**Slice awareness.** Multi-sliced textures (`spriteImportMode == Multiple`) are processed **per slice by default** — pixels just outside each slice rect are treated as transparent, so frame boundaries get proper edge bevels. This is what animation strips want (e.g. `powershaft.png`, 80×16 sliced into 5 frames).

For *spatial* sheets — slices that abut each other in the world (elevator/platform stacks) — set the **merged-normals flag** via `Assets → Toggle Merged Normals` on the texture. The flag is stored as `normals=merged` in `TextureImporter.userData`; merged sheets are processed as one big sprite so inter-slice pixel boundaries remain interior. Each slice samples its own sub-region of the resulting normal map at runtime (secondary textures are shared across all slices in a sheet).

`Assets → Slice Vertical Building Sheet` is the companion authoring tool: given a 16×32 or 16×48 texture, it sets up bottom→top slices named `{stem}_b` / `{stem}_m` / `{stem}_t` (centred pivot, PPU=16) and turns on the merged-normals flag. Used to consolidate `{name}_b.png`/`_m.png`/`_t.png` into a single `{name}.png` (or `{name}_s.png` if a 1×1 `{name}.png` already exists). After slicing, run normal map generation; existing standalone `{name}_b/_m/_t.png` files become unused and can be deleted.

Post-pass for fire sprites: each `_f.png` is wired as its own `_EmissionMap` (self-reference — all visible fire pixels emit). If a `{stem}_e.png` companion exists alongside the `_f.png`, that takes precedence.

**Companion file conventions** (inside `Assets/Resources/Sprites/Buildings/`):

| Suffix   | Purpose                          | Normal maps? | Emission wiring                                   |
|----------|----------------------------------|--------------|----------------------------------------------------|
| `_n.png` | Generated normal map             | N/A          | —                                                  |
| `_e.png` | Emission mask                    | Skipped      | Assigned as `_EmissionMap` on base sprite           |
| `_f.png` | Fire art (separate child sprite) | Skipped      | Self-reference `_EmissionMap` (all pixels emit)     |

---

### Fire sprites

Fire art (torch flame, fireplace fire) lives in a **separate child GameObject**, not baked into the base building sprite. This lets fire disappear entirely when the light is off.

**Setup** (`Structure.cs` constructor): if `Resources/Sprites/Buildings/{name}_f` exists, a child `"fire"` GO is created with its own `SpriteRenderer` at the same `sortingOrder` as the parent. Starts inactive.

**Toggle** (`LightSource.cs` Update): `building.fireGO.SetActive(_lastEmissionScale > 0.05f)`. Fire visibility tracks the emission scale — appears/disappears in sync with the twilight emission fade rather than popping on/off abruptly. Hidden when: daytime, out of fuel, building disabled or broken.

**Emission**: `LightSource` retargets `_EmissionScale` MPB writes to `building.fireSR` when present (falls back to parent SR for non-fire emissive buildings). Combined with the `_EmissionMap` self-reference from the generator, fire pixels stay full brightness through `LightComposite`'s multiply.
