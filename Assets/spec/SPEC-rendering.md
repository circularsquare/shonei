# Shonei — Rendering & Lighting

## Rendering & Layers

### Sorting orders (authoritative — keep this up to date)

| sortingOrder | What |
|---|---|
| -10 | Background cave wall (`CaveAtmosphere`) |
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
   - `0.80–1.0` — shadow caster. The range encodes **edge depth** for underground darkening: `1.0` = at exposed tile surface (fully lit), `0.80` = deep interior (darkened to `deepFloor`). Non-tile shadow casters output `1.0`. Extracted as `saturate((alpha - 0.80) / 0.20)` in `LightComposite`.
   - `0.5` — lit-only (full light, no shadow cast)
   - `0.3` — directional-only (sun + ambient only; `LightCircle` skips torch for these pixels)
   - `0.0` — no sprite (flat-normal fallback in light shaders)

   **Jagged tile edges**: NormalsCapture includes `TileEdge.hlsl` and reads `_AdjacencyMask` (float 0–15, set via MPB on tile SpriteRenderers). Pixels outside the procedural jagged edge profile are discarded. The override material defaults `_AdjacencyMask = 15` (no clipping) so non-tile sprites are unaffected.

   Three draw calls in order (later overwrites earlier where they overlap, `Blend Off`):
   - Pass 2 (`directionalOnlyLayers`): alpha = 0.3 — drawn first so shadow-caster pass can't overwrite
   - Pass 1 (`litLayers & ~shadowCasterLayers & ~directionalOnlyLayers`): alpha = 0.5
   - Pass 0 (`litLayers & shadowCasterLayers & ~directionalOnlyLayers`): alpha = `lerp(0.80, 1.0, _NormalMap.a)` where `_NormalMap.a` carries edge-distance falloff from `TileNormalMaps`

2. **LightPass** (`AfterRenderingTransparents`) — `ConfigureTarget(LightRTId)` in `OnCameraSetup` binds the temp RT (required for the clear and for `cmd.Blit` to target it correctly across all cameras). Ambient light is split into two parts:
   - **Base ambient** (universal): `SunController.GetAmbientColor() × 0.5` — clears the light RT. Present everywhere, even deep underground.
   - **Sky ambient** (exposure-gated): `SunController.GetAmbientColor() × 0.5` — blitted via `LightAmbientFill.shader` which samples `_SkyExposureTex` (set by `CaveAtmosphere`). Full contribution above ground, zero underground.
   
   Then draws:
   - Point lights (torches, etc.): `cmd.DrawMesh` per-light quad scaled to `outerRadius×2`, screen blend (`BlendOp Add, Blend One OneMinusSrcColor`), radial falloff × NdotL. **Skips pixels where normals RT alpha is 0–0.4** (directional-only tier).
   - Sun (directional): `cmd.Blit(null, LightRTId, sunMat)`, additive blend (`BlendOp Add, Blend One One`), NdotL with `_SunDir` + 16-step shadow march. **Must use `cmd.Blit`, not `cmd.DrawMesh`** — DrawMesh silently fails to write to the temp RT for cameras without PixelPerfectCamera (e.g. BackgroundCamera). Blit handles its own fullscreen geometry and RT binding internally, bypassing the issue.

3. **Composite** — `cmd.Blit(lightRT, scene, LightComposite)` multiplies scene by light map (`Blend DstColor Zero`). **No-op (returns `(1,1,1,1)`) for pixels with normals RT alpha < 0.25** (empty sky/background); those pixels are instead tinted by `BackgroundCamera.backgroundColor`. **Underground darkening**: shadow-caster pixels (alpha > 0.75) are additionally scaled by `lerp(deepFloor, 1.0, edgeDepth)` — deep tile interiors get dimmed to `deepFloor` brightness (tunable on `LightFeature` inspector, default 0.2). After deepFloor dimming, a `max(light, baseAmbient)` clamp ensures tile interiors never go below the universal base ambient.

**LightFeature skips cameras** where `cullingMask == 0` or where `(cullingMask & (litLayers | directionalOnlyLayers | waterLayer)) == 0` — i.e. cameras that see no sprites participating in the normals RT. The `UnlitOverlayCamera` (see Sky/background below) hits the second check because the Unlit layer is excluded from all three masks.

### URP render target gotchas

- **`ConfigureTarget` vs `cmd.SetRenderTarget`**: URP docs say to use `ConfigureTarget` in `OnCameraSetup` rather than raw `cmd.SetRenderTarget` in `Execute`. `ConfigureTarget` integrates with URP's internal RT management and ensures the target is bound correctly for all cameras. Without it, `ClearRenderTarget` and `cmd.Blit` may write to stale/wrong targets.
- **`cmd.DrawMesh` can silently fail on some cameras**: DrawMesh output may appear in Frame Debugger but produce no visible output on the temp RT for certain cameras (observed with BackgroundCamera, which lacks PixelPerfectCamera). The root cause is unclear but likely related to URP's internal state management. **Workaround**: use `cmd.Blit` for fullscreen passes (sun). Point lights still use DrawMesh and work because BackgroundCamera has no point-light-receiving sprites (directional-only tier skips torch contribution in LightCircle).
- **Temp RT format**: `_CapturedNormalsRT` must be **ARGB32** (explicitly set in `OnCameraSetup`). The camera's default HDR format (B10G11R11) has no alpha channel, which breaks the tier encoding.
- **`_AdjacencyMask` default on override material**: `NormalsCapturePass` sets `mat.SetFloat("_AdjacencyMask", 15f)` on the override material so non-tile sprites don't get jagged-edge clipped. If the material is ever recreated (shader hot reload), this default is re-applied in the constructor — but if you change when/how the pass is constructed, make sure this line survives or every non-tile sprite will be invisible.

### Sky / background

Three cameras render in depth order:

| Camera | Depth | Clear Flags | Culling Mask | Notes |
|--------|-------|-------------|--------------|-------|
| `BackgroundCamera` | 0 | Solid Color | Background layer | `backgroundColor` set to `baseSkyColor × GetAmbientColor()` each frame — sky darkens at night |
| Main Camera | 1 | Don't Clear | Everything except Unlit | PixelPerfectCamera; lighting composite applied here |
| `UnlitOverlayCamera` | 2 | Don't Clear | Unlit only | Renders after composite — sprites on the **Unlit** layer appear at full brightness, unaffected by lighting. Has `MatchCameraZoom` component to sync `assetsPPU` from Main Camera. LightFeature pipeline is skipped for this camera entirely. |

**Unlit layer pattern**: any sprite that should always appear at full brightness (tile highlights, selection overlays, debug markers) goes on the `Unlit` layer. Keep it excluded from `litLayers`, `shadowCasterLayers`, and `directionalOnlyLayers` in the LightFeature Inspector.

### Cave atmosphere (underground lighting + background)

`CaveAtmosphere.cs` — auto-created singleton, initialized by `WorldController.GenerateDefault()` and `SaveSystem.Load()`.

**Two-part ambient model**: ambient light = base ambient (universal) + sky ambient (exposure-gated).
- **Base ambient** = `GetAmbientColor() × 0.5` — clears the light RT. Always present, even underground. The composite shader's `max(light, baseAmbient)` clamp ensures deepFloor dimming never goes below this.
- **Sky ambient** = `GetAmbientColor() × 0.5` — added via `LightAmbientFill.shader`, modulated by `_SkyExposureTex`. Full where `!hasBackgroundWall`, zero where `hasBackgroundWall`.

**Per-tile background wall**: each `Tile` has a `hasBackgroundWall` bool (with `cbBackgroundWallChanged` callback). During world generation, tiles at y ≤ 45 are given a background wall. The flag is saved/loaded in `TileSaveData`.

**Sky exposure texture** (`_SkyExposureTex`): R8, nx×ny, bilinear filtered. Set as global shader texture. Driven by `tile.hasBackgroundWall` — 255 where no wall (sky), 0 where wall (underground). Rebuilt when any tile's background wall changes.

**Background cave wall sprite**: a world-spanning sprite on the **Default layer** at **sorting order −10** (behind tiles at 0). Uses an RGBA32 texture (nx×ny, PPU=1) — cave wall color where `hasBackgroundWall`, transparent elsewhere. Participates in normal lighting (sun, torches) but receives no sky ambient because `_SkyExposureTex` is 0 for `hasBackgroundWall` tiles. Rebuilt on background wall or tile type change via dirty flag.

### Key files

All lighting C# scripts and shaders live in `Assets/Lighting/`.

| File | Role |
|------|------|
| `LightFeature.cs` | `ScriptableRendererFeature` containing `NormalsCapturePass` + `LightPass` |
| `LightSource.cs` | Component: `lightColor`, `intensity`, `outerRadius`, `innerRadius`, `lightHeight`, `isDirectional`. Registers itself in a static list read by `LightPass`. |
| `SunController.cs` | Orbiting sun, sky color, `GetAmbientColor()`, `GetSunDirection()`. Sun child has a `LightSource (isDirectional=true)`. |
| `NormalsCapture.shader` | Tangent→world normal transform for flat 2D sprites: `(x, y, z) → (x, y, −z)`. Includes `TileEdge.hlsl` for jagged clip + edge depth alpha. |
| `TileEdge.hlsl` | Shared HLSL include: PCG hash noise + `TileEdgeClip()` for jagged tile borders. Used by NormalsCapture and TileSprite. |
| `TileSprite.shader` | Custom tile sprite shader with jagged-edge clipping. Assigned to tile SpriteRenderers by WorldController. |
| `TileNormalMaps.cs` | 16 cached normal maps (4-bit adjacency). Alpha channel encodes edge-distance falloff for light penetration. Also sets `_AdjacencyMask` via MPB. |
| `LightCircle.shader` | Point light pass: radial falloff × NdotL. |
| `LightSun.shader` | Directional sun pass: fullscreen NdotL + shadow march. |
| `LightAmbientFill.shader` | Fullscreen pass: writes `skyAmbient × exposure` per pixel. Additive onto base-ambient-cleared RT. |
| `LightComposite.shader` | Multiply blit onto scene + underground darkening via edgeDepth + base ambient floor clamp. |
| `CaveAtmosphere.cs` | Manages underground atmosphere: sky exposure texture (R8, per-tile, driven by `hasBackgroundWall`) and background cave wall sprite (Default layer, sort -10). Auto-created singleton. |

`Assets/Editor/SpriteNormalMapGenerator.cs` — sprite normal map batch tool (must stay in `Editor/`).

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
