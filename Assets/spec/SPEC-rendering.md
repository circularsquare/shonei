# Shonei — Rendering & Lighting

## Rendering & Layers

### Sorting orders (authoritative — keep this up to date)

| sortingOrder | What |
|---|---|
| -10 | Background tile (`BackgroundTile`) |
| 0 | Tiles |
| 1 | Roads (depth-3 structures) |
| 10 | Buildings (depth-0 structures) |
| 11 | Platforms (depth-1 structures); also clock hand (one above its body) |
| 12 | Water overlay sprite (`WaterController`) |
| 30 | Items in storage/inventory display |
| 40 | Foreground structures (depth-2: stairs, ladders, torches) |
| 48 | Animal tail (paper-doll part) |
| 49 | Animal back foot (paper-doll part) |
| 50 | Animal body (paper-doll part) |
| 51 | Animal front foot (paper-doll part) |
| 52 | Animal arm (paper-doll part) |
| 55–57 | Clothing overlays (per-part children: body 55, foot 56, arm 57) |
| 60 | Plants |
| 65 | Falling items (mid-air animation) |
| 70 | Items on floor |
| 100 | Blueprints |
| 200 | Build preview (mouse cursor ghost) |

### Structure depth layers

Structures render in four depth layers per tile. Each tile holds `Structure[] structs` and `Blueprint[] blueprints`, both indexed by depth int:

| Depth | `structs[d]` | Contents | Sprite position | sortingOrder |
|-------|-------------|----------|----------------|-------------|
| 0 | building layer | Buildings, plants (`Building`/`Plant`) | `(x, y)` | 10 |
| 1 | platform layer | Platforms | `(x, y)` | 11 |
| 2 | foreground layer | Stairs, ladders, torches | `(x, y)` | 20 |
| 3 | road layer | Roads | `(x, y−1/8)` — sits on tile surface | 1 |

`tile.building` is a convenience property: `structs[0] as Building` (Plant extends Building, so both are accessible through it). Multiple layers can coexist on the same tile. `GetBlueprintAt(int depth)` / `SetBlueprintAt(int depth, Blueprint bp)` directly index into `blueprints[]`.

---

## Lighting

Custom `ScriptableRendererFeature` pipeline — no URP Light2Ds used. Final result is Multiply-blitted onto the scene.

### Render pipeline (per frame)

1. **NormalsCapturePass** (`BeforeRenderingTransparents`) — draws sprites with `Hidden/NormalsCapture` override into `_CapturedNormalsRT` (format: **ARGB32** — must have alpha; camera's default HDR format has none). Outputs world-space normals packed 0–1 in RGB. **Alpha encodes lighting tier + edge depth:**
   - `0.80–1.0` — solid tile. The range encodes **edge depth** for underground darkening: `1.0` = at exposed tile surface (fully lit), `0.80` = deep interior (darkened to `deepFloor`). Extracted as `saturate((alpha - 0.80) / 0.20)` in `LightComposite`. (Previously also used for shadow casting — ray march is commented out in `LightSun.shader`.)
   - `0.5` — lit-only (full light)
   - `0.3` — directional-only (sun + ambient only; `LightCircle` skips torch for these pixels)
   - `0.0` — no sprite (flat-normal fallback in light shaders)

   **Tile border clipping**: Tiles use pre-baked 20×20 sprites (from `TileSpriteCache`) whose alpha already encodes the border shape. NormalsCapture clips on `_MainTex` alpha for both tiles and non-tiles — no per-pixel atlas lookup needed.

   Draw calls in order (later overwrites earlier where they overlap, `Blend Off`):
   - Background (`backgroundLayer`): `NormalsCaptureBackground` override, pass 1 (alpha = 0.5, lit-only). Clips transparent top pixels so they read as sky. Drawn earliest so tiles/sprites overwrite.
   - Pass 2 (`directionalOnlyLayers`): alpha = 0.3 — drawn next so shadow-caster pass can't overwrite
   - Pass 1 (`litLayers & ~shadowCasterLayers & ~directionalOnlyLayers`): alpha = 0.5
   - Pass 0 (`litLayers & shadowCasterLayers & ~directionalOnlyLayers`): alpha = `lerp(0.80, 1.0, _NormalMap.a)` where `_NormalMap.a` carries edge-distance falloff from `TileNormalMaps`

2. **LightPass** (`AfterRenderingTransparents`) — `ConfigureTarget(LightRTId)` in `OnCameraSetup` binds the temp RT (required for the clear and for `cmd.Blit` to target it correctly across all cameras). Ambient light is split into two parts:
   - **Deep ambient** (constant): `LightFeature.deepAmbientColor` — clears the light RT. Present everywhere, even deep underground. Not affected by time of day. Tunable on `LightFeature` inspector.
   - **Sky light** (time-varying, distance falloff): `SunController.GetAmbientColor()` — blitted via `LightAmbientFill.shader` which samples `_SkyExposureTex` (set by `SkyExposure`). Falls off with distance from sky-exposed tiles. Changes with day/night cycle.
   
   Then draws:
   - Point lights (torches, etc.): `cmd.DrawMesh` per-light quad scaled to `outerRadius×2`, screen blend (`BlendOp Add, Blend One OneMinusSrcColor`), radial falloff × NdotL. **Skips pixels where normals RT alpha is 0–0.4** (directional-only tier).
   - Sun (directional): `cmd.Blit(null, LightRTId, sunMat)`, additive blend (`BlendOp Add, Blend One One`), NdotL with `_SunDir`. Shadow ray march is **disabled** (commented out in `LightSun.shader` for performance). **Must use `cmd.Blit`, not `cmd.DrawMesh`** — DrawMesh silently fails to write to the temp RT for cameras without PixelPerfectCamera (e.g. SkyCamera). Blit handles its own fullscreen geometry and RT binding internally, bypassing the issue.

3. **Composite** — `cmd.Blit(lightRT, scene, LightComposite)` multiplies scene by light map (`Blend DstColor Zero`). Empty sky/background pixels (normals RT alpha < 0.25) use a precomputed `_SkyLightColor` (sun + time-of-day ambient, no sky-exposure modulation, no point lights), blended via `skyLightBlend` (tunable on `LightFeature` inspector, default 1.0). Those pixels' base color comes from `SkyCamera.backgroundColor`. **Underground darkening**: solid-tile pixels (alpha > 0.75) are additionally scaled by `lerp(deepFloor, 1.0, edgeDepth)` — deep tile interiors get dimmed to `deepFloor` brightness (tunable on `LightFeature` inspector, default 0.2). After deepFloor dimming, a `max(light, deepAmbient)` clamp ensures tile interiors never go below the universal deep ambient.

**LightFeature skips cameras** where `cullingMask == 0` or where `(cullingMask & (litLayers | directionalOnlyLayers | waterLayer | backgroundLayer)) == 0` — i.e. cameras that see no sprites participating in the normals RT. The `UnlitOverlayCamera` (see Sky/background below) hits this check because the Unlit layer is excluded from all four masks.

### URP render target gotchas

- **`ConfigureTarget` vs `cmd.SetRenderTarget`**: URP docs say to use `ConfigureTarget` in `OnCameraSetup` rather than raw `cmd.SetRenderTarget` in `Execute`. `ConfigureTarget` integrates with URP's internal RT management and ensures the target is bound correctly for all cameras. Without it, `ClearRenderTarget` and `cmd.Blit` may write to stale/wrong targets.
- **`cmd.DrawMesh` can silently fail on some cameras**: DrawMesh output may appear in Frame Debugger but produce no visible output on the temp RT for certain cameras (observed with SkyCamera, which lacks PixelPerfectCamera). The root cause is unclear but likely related to URP's internal state management. **Workaround**: use `cmd.Blit` for fullscreen passes (sun). Point lights still use DrawMesh and work because SkyCamera has no point-light-receiving sprites (directional-only tier skips torch contribution in LightCircle).
- **Temp RT format**: `_CapturedNormalsRT` must be **ARGB32** (explicitly set in `OnCameraSetup`). The camera's default HDR format (B10G11R11) has no alpha channel, which breaks the tier encoding.
- **Tile border clipping**: Previously `NormalsCapturePass` set `_AdjacencyMask` and `_BorderAtlas` defaults on the override material. This is no longer needed — tiles use pre-baked 20×20 sprites (from `TileSpriteCache`) and NormalsCapture simply clips on `_MainTex` alpha.

### Sky / background

Three cameras render in depth order:

| Camera | Depth | Clear Flags | Culling Mask | Notes |
|--------|-------|-------------|--------------|-------|
| `SkyCamera` | 0 | Solid Color | Sky layer | `backgroundColor` set to `baseSkyColor × GetAmbientColor()` each frame — sky darkens at night |
| Main Camera | 1 | Don't Clear | Everything except Unlit | PixelPerfectCamera; lighting composite applied here |
| `UnlitOverlayCamera` | 2 | Don't Clear | Unlit only | Renders after composite — sprites on the **Unlit** layer appear at full brightness, unaffected by lighting. Has `MatchCameraZoom` component to sync `assetsPPU` from Main Camera. LightFeature pipeline is skipped for this camera entirely. |

**Unlit layer pattern**: any sprite that should always appear at full brightness (tile highlights, selection overlays, debug markers) goes on the `Unlit` layer. Keep it excluded from `litLayers`, `shadowCasterLayers`, and `directionalOnlyLayers` in the LightFeature Inspector.

### Sky exposure (`SkyExposure.cs`)

`Assets/Lighting/SkyExposure.cs` — scene singleton (under Lighting), initialized by `WorldController.GenerateDefault()` and `SaveSystem.Load()`.

**Two-part ambient model**: ambient light = deep ambient (constant) + sky light (time-varying, distance falloff).
- **Deep ambient** = `LightFeature.deepAmbientColor` — constant color, clears the light RT. Always present, even deep underground. Not affected by day/night cycle.
- **Sky light** = `GetAmbientColor()` — added via `LightAmbientFill.shader`, modulated by `_SkyExposureTex`. Emitted from sky-exposed tiles (`!hasBackground`), falls off with Chebyshev distance into surrounding material. Falloff depth = `LightFeature.lightPenetrationDepth` (shared with `TileNormalMaps` sub-tile edge depth). Changes with day/night cycle. Sun is also modulated by `_SkyExposureTex` in `LightSun.shader`.

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
| `LightFeature.cs` | `ScriptableRendererFeature` containing `NormalsCapturePass` + `LightPass`. Inspector tunables: `ambientNormal`, `lightPenetrationDepth`, `deepAmbientColor`, `skyLightBlend`, layer masks. |
| `LightSource.cs` | Component: `lightColor`, `intensity`, `outerRadius`, `innerRadius`, `lightHeight`, `isDirectional`. Registers itself in a static list read by `LightPass`. `sunModulated` (default false): when true, `SunController` overrides intensity by time of day (torches/fireplaces). Non-modulated lights keep their set intensity always. |
| `SunController.cs` | Orbiting sun, sky color, `GetAmbientColor()`, `GetSunDirection()`. Sun child has a `LightSource (isDirectional=true)`. Inspector tunables: orbit, twilight timing, sky/sun/ambient color gradients, `sunIntensityNoon`, `ambientBrightnessMin`/`ambientBrightnessRange`. |
| `NormalsCapture.shader` | Tangent→world normal transform for flat 2D sprites: `(x, y, z) → (x, y, −z)`. Clips on `_MainTex` alpha (tiles have pre-baked borders, non-tiles use sprite alpha). |
| `TileSprite.shader` | Simple tile sprite shader: samples `_MainTex` (pre-baked 20×20 from `TileSpriteCache`), clips transparent pixels. Assigned to tile SpriteRenderers by WorldController. |
| `TileSpriteCache.cs` | Bakes 20×20 tile sprites at load time from 32×32 border atlases. 16 cardinal-mask variants per tile type. PPU=16 → sprites natively span 1.25 units. |
| `TileNormalMaps.cs` | 256 cached 20×20 normal maps (8-bit adjacency). 4px gradient bevel on exposed edges within the 16×16 interior; border pixels get flat normals. Alpha channel encodes edge-distance falloff for light penetration. |
| `LightCircle.shader` | Point light pass: radial falloff × NdotL. |
| `LightSun.shader` | Directional sun pass: fullscreen NdotL × sky exposure. Shadow ray march disabled (commented out for performance). |
| `LightAmbientFill.shader` | Fullscreen pass: writes `skyLight × exposure` per pixel. Max blend onto deep-ambient-cleared RT. |
| `LightComposite.shader` | Multiply blit onto scene + edge-depth blending toward deepAmbient for deep tile interiors. |
| `SkyExposure.hlsl` | Shared HLSL include: declares `_CamWorldBounds`, `_GridSize`, `_SkyExposureTex` and provides `SampleSkyExposure(screenUV)`. Used by LightAmbientFill and LightSun. |
| `BackgroundTile.shader` | Tiles `_WallTex` or `_WallTopTex` (selected by mask green channel) at world-space UVs, masked by `_MainTex`. Tagged `Universal2D` for normals capture. |
| `NormalsCaptureBackground.shader` | Normals capture override for background. Samples `_BackgroundTex`/`_BackgroundTopTex` (globals set by `BackgroundTile.cs`) and clips transparent pixels — fixes jagged top-edge lighting. |
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

**Tile normal maps** (`TileNormalMaps.cs`): 256 procedural 20×20 variants (8-bit adjacency mask: 4 cardinal + 4 diagonal). The 16×16 interior (pixels 2–17) has 4px gradient bevel on exposed edges; border pixels (0–1 and 18–19) get flat normals and full edge depth. Applied via `MaterialPropertyBlock` on tile `SpriteRenderer`s.

**Tile border atlases** (source format): 32×32 artist-authored textures in `Assets/Resources/Sprites/Tiles/Sheets/{name}.png`. Layout: main 16×16 at (8,8), top/bottom 16×4 borders at (8,0)/(8,28), left/right 4×16 borders at (0,8)/(28,8), four 4×4 corner pieces at (0,0)/(28,0)/(0,28)/(28,28). Columns 1,6 and rows 1,6 are empty separators. **Not sampled at runtime** — `TileSpriteCache` reads pixel data at load time to bake 16 cardinal-mask variants per tile type as 20×20 Sprites (PPU=16 → 1.25 units). Textures must have Read/Write enabled (`TileSpritePostprocessor` handles this automatically).

**Sprite normal maps** (`SpriteNormalMapGenerator.cs`): editor tool (**Tools → Generate All Sprite Normal Maps**) batch-processes `Assets/Resources/Sprites/`. For each texture:
1. Generates `_n.png` — edge pixels get outward normals, interior gets flat forward normal.
2. Imports as `Default` / `Uncompressed` RGBA32 (not NormalMap type — must stay plain packed 0–1).
3. Auto-assigns as `_NormalMap` secondary texture on the source sprite importer.
