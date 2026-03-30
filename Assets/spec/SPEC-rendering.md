# Shonei — Rendering & Lighting

## Rendering & Layers

### Sorting orders (authoritative — keep this up to date)

| sortingOrder | What |
|---|---|
| 0 | Tiles |
| 1 | Roads (depth-3 structures) |
| 10 | Buildings (depth-0 structures) |
| 11 | Platforms (depth-1 structures); also clock hand (one above its body) |
| 12 | Water overlay sprite (`WaterController`) |
| 30 | Items in storage/inventory display |
| 50 | Animals |
| 51 | Clothing overlay (child SpriteRenderer on Animal) |
| 60 | Plants |
| 65 | Falling items (mid-air animation) |
| 70 | Items on floor |
| 80 | Foreground structures (depth-2: stairs, ladders, torches) |
| 100 | Blueprints |
| 200 | Build preview (mouse cursor ghost) |

### Structure depth layers

Structures render in four depth layers per tile. Each tile holds `Structure[] structs` and `Blueprint[] blueprints`, both indexed by depth int:

| Depth | `structs[d]` | Contents | Sprite position | sortingOrder |
|-------|-------------|----------|----------------|-------------|
| 0 | building layer | Buildings, plants (`Building`/`Plant`) | `(x, y)` | 10 |
| 1 | platform layer | Platforms | `(x, y)` | 11 |
| 2 | foreground layer | Stairs, ladders, torches | `(x, y)` | 80 |
| 3 | road layer | Roads | `(x, y−1/8)` — sits on tile surface | 1 |

`tile.building` is a convenience property: `structs[0] as Building` (Plant extends Building, so both are accessible through it). Multiple layers can coexist on the same tile. `GetBlueprintAt(int depth)` / `SetBlueprintAt(int depth, Blueprint bp)` directly index into `blueprints[]`.

---

## Lighting

Custom `ScriptableRendererFeature` pipeline — no URP Light2Ds used. Final result is Multiply-blitted onto the scene.

### Render pipeline (per frame)

1. **NormalsCapturePass** (`BeforeRenderingTransparents`) — draws sprites with `Hidden/NormalsCapture` override into `_CapturedNormalsRT` (format: **ARGB32** — must have alpha; camera's default HDR format has none). Outputs world-space normals packed 0–1 in RGB. **Alpha encodes lighting tier:**
   - `1.0` — shadow caster (full light, casts shadows)
   - `0.5` — lit-only (full light, no shadow cast)
   - `0.3` — directional-only (sun + ambient only; `LightCircle` skips torch for these pixels)
   - `0.0` — no sprite (flat-normal fallback in light shaders)

   Three draw calls in order (later overwrites earlier where they overlap, `Blend Off`):
   - Pass 2 (`directionalOnlyLayers`): alpha = 0.3 — drawn first so shadow-caster pass can't overwrite
   - Pass 1 (`litLayers & ~shadowCasterLayers & ~directionalOnlyLayers`): alpha = 0.5
   - Pass 0 (`litLayers & shadowCasterLayers & ~directionalOnlyLayers`): alpha = 1.0

2. **LightPass** (`AfterRenderingTransparents`) — `ConfigureTarget(LightRTId)` in `OnCameraSetup` binds the temp RT (required for the clear and for `cmd.Blit` to target it correctly across all cameras). Clears light RT to `SunController.GetAmbientColor()`, then draws:
   - Point lights (torches, etc.): `cmd.DrawMesh` per-light quad scaled to `outerRadius×2`, screen blend (`BlendOp Add, Blend One OneMinusSrcColor`), radial falloff × NdotL. **Skips pixels where normals RT alpha is 0–0.4** (directional-only tier).
   - Sun (directional): `cmd.Blit(null, LightRTId, sunMat)`, additive blend (`BlendOp Add, Blend One One`), NdotL with `_SunDir` + 16-step shadow march. **Must use `cmd.Blit`, not `cmd.DrawMesh`** — DrawMesh silently fails to write to the temp RT for cameras without PixelPerfectCamera (e.g. BackgroundCamera). Blit handles its own fullscreen geometry and RT binding internally, bypassing the issue.

3. **Composite** — `cmd.Blit(lightRT, scene, LightComposite)` multiplies scene by light map (`Blend DstColor Zero`). **No-op (returns `(1,1,1,1)`) for pixels with normals RT alpha < 0.25** (empty sky/background); those pixels are instead tinted by `BackgroundCamera.backgroundColor`.

**LightFeature skips cameras** where `cullingMask == 0` or where `(cullingMask & (litLayers | directionalOnlyLayers | waterLayer)) == 0` — i.e. cameras that see no sprites participating in the normals RT. The `UnlitOverlayCamera` (see Sky/background below) hits the second check because the Unlit layer is excluded from all three masks.

### URP render target gotchas

- **`ConfigureTarget` vs `cmd.SetRenderTarget`**: URP docs say to use `ConfigureTarget` in `OnCameraSetup` rather than raw `cmd.SetRenderTarget` in `Execute`. `ConfigureTarget` integrates with URP's internal RT management and ensures the target is bound correctly for all cameras. Without it, `ClearRenderTarget` and `cmd.Blit` may write to stale/wrong targets.
- **`cmd.DrawMesh` can silently fail on some cameras**: DrawMesh output may appear in Frame Debugger but produce no visible output on the temp RT for certain cameras (observed with BackgroundCamera, which lacks PixelPerfectCamera). The root cause is unclear but likely related to URP's internal state management. **Workaround**: use `cmd.Blit` for fullscreen passes (sun). Point lights still use DrawMesh and work because BackgroundCamera has no point-light-receiving sprites (directional-only tier skips torch contribution in LightCircle).
- **Temp RT format**: `_CapturedNormalsRT` must be **ARGB32** (explicitly set in `OnCameraSetup`). The camera's default HDR format (B10G11R11) has no alpha channel, which breaks the tier encoding.

### Sky / background

Three cameras render in depth order:

| Camera | Depth | Clear Flags | Culling Mask | Notes |
|--------|-------|-------------|--------------|-------|
| `BackgroundCamera` | 0 | Solid Color | Background layer | `backgroundColor` set to `baseSkyColor × GetAmbientColor()` each frame — sky darkens at night |
| Main Camera | 1 | Don't Clear | Everything except Unlit | PixelPerfectCamera; lighting composite applied here |
| `UnlitOverlayCamera` | 2 | Don't Clear | Unlit only | Renders after composite — sprites on the **Unlit** layer appear at full brightness, unaffected by lighting. Has `MatchCameraZoom` component to sync `assetsPPU` from Main Camera. LightFeature pipeline is skipped for this camera entirely. |

**Unlit layer pattern**: any sprite that should always appear at full brightness (tile highlights, selection overlays, debug markers) goes on the `Unlit` layer. Keep it excluded from `litLayers`, `shadowCasterLayers`, and `directionalOnlyLayers` in the LightFeature Inspector.

### Key files

All lighting C# scripts and shaders live in `Assets/Lighting/`.

| File | Role |
|------|------|
| `LightFeature.cs` | `ScriptableRendererFeature` containing `NormalsCapturePass` + `LightPass` |
| `LightSource.cs` | Component: `lightColor`, `intensity`, `outerRadius`, `innerRadius`, `lightHeight`, `isDirectional`. Registers itself in a static list read by `LightPass`. |
| `SunController.cs` | Orbiting sun, sky color, `GetAmbientColor()`, `GetSunDirection()`. Sun child has a `LightSource (isDirectional=true)`. |
| `NormalsCapture.shader` | Tangent→world normal transform for flat 2D sprites: `(x, y, z) → (x, y, −z)`. |
| `LightCircle.shader` | Point light pass: radial falloff × NdotL. |
| `LightSun.shader` | Directional sun pass: fullscreen NdotL. |
| `LightComposite.shader` | Multiply blit onto scene. |

`Assets/Editor/SpriteNormalMapGenerator.cs` — sprite normal map batch tool (must stay in `Editor/`).

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
- `BackgroundCamera` culling mask (otherwise water appears in the sky)

**Lighting**: water is lit via a dedicated path. `LightFeature` has a `waterLayer` field (set to `Water` in the Inspector) which triggers a separate `DrawRenderers` call in `NormalsCapturePass` using `Hidden/NormalsCaptureWater` (pass 1, lit-only, alpha=0.5). That shader samples the global `_WaterSurfaceTex` (set each tick by `WaterController.UpdateSurfaceMask()`) for transparency, discarding pixels with no water. Outputs flat forward normals. This means water darkens at night and receives ambient light, but torch NdotL is minimal (flat normal faces away from scene).

**sortingOrder**: `12` — above buildings (10) and platforms (11) so decorative water zones render on top of building sprites.

### Decorative water zones

Building sprites can have pixels that render as water shimmer without participating in the fluid simulation. This is used for e.g. the fountain basin.

**Marker color**: `R=0 G=0 B=255 A=2` (exact RGBA match). Paint pixels this color in a building sprite to mark them as water. Alpha=2 makes them nearly invisible so the water shader colour shows through cleanly.

**Requirements**: the building sprite's texture must have **Read/Write Enabled** in its Unity Import Settings. `Assets/Editor/BuildingSpritePostprocessor.cs` auto-enables this for all textures under `Resources/Sprites/Buildings/` on import — reimport the folder once after adding the postprocessor.

**How it works**: `WaterController.ScanWaterPixels()` scans the sprite at structure construction time and stores matching local pixel offsets on the `Structure`. `StructController.Place()` registers them with `WaterController.RegisterDecorativeWater()`, which converts offsets to world-pixel coordinates (accounting for `mirrored` flipX). Each `UpdateSurfaceMask()` tick, registered zones are overlaid into `_surfaceBytes` as `127` (interior shimmer). If the structure has a `Reservoir`, the zone is only shown when `reservoir.HasFuel()` — dry fountain = no water pixels.

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

**Encoding**: world-space, packed 0–1. Flat camera-facing sprite = `(0,0,−1)` → `(0.5, 0.5, 0.0)`. Black = no sprite, shader uses flat fallback. No Y-flip on screen UV (DrawRenderers and the light pass projection both use OpenGL convention, V=0 at bottom).

**Tile normal maps** (`TileNormalMaps.cs`): 16 procedural variants (4-bit adjacency mask). Exposed edges bevel outward; interior stays flat. Applied via `MaterialPropertyBlock` on tile `SpriteRenderer`s.

**Sprite normal maps** (`SpriteNormalMapGenerator.cs`): editor tool (**Tools → Generate All Sprite Normal Maps**) batch-processes `Assets/Resources/Sprites/`. For each texture:
1. Generates `_n.png` — edge pixels get outward normals, interior gets flat forward normal.
2. Imports as `Default` / `Uncompressed` RGBA32 (not NormalMap type — must stay plain packed 0–1).
3. Auto-assigns as `_NormalMap` secondary texture on the source sprite importer.
