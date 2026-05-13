# Shonei — Rendering & Lighting

## Rendering & Layers

### Sorting orders (authoritative — keep this up to date)

| sortingOrder | What |
|---|---|
| -10 | Background tile (`BackgroundTile`) |
| -5 | Water overlay sprite (`WaterController`) — sits behind tiles so the bleed-into-solid-neighbour pixels (see Water Rendering §) only show through the tile sprite's transparent bevel gaps. Above the background wall so cave water still reads against dirt. |
| 0..−4 | Tile bodies (chunked mesh per chunk × type — see §Tile body rendering). Within a chunk, all tiles of one type share one `MeshRenderer` at `sortingOrder = −rank(type)`, ranks 0..k−1 ascending by tile-type id (dirt 0, sand −1, limestone −2, granite −3, slate −4). Lower-id wins the soft-edge contest — its overhang draws on top of higher-id Main extensions. |
| 1 | Roads (depth-3 structures) |
| 2 | Tile snow cover (`SnowAccumulationSystem`). Sits above the tile body so accumulated snow covers the underlying ground; on roaded tiles snow draws on top, reading as a snow-covered road. Mutually exclusive at runtime with the grass overlay (snow accumulation snapshots and clears the overlay mask), so the ordering between snow (2) and overlay (11) doesn't visually matter. |
| 5 | Power shafts (depth-4 structures) — render behind buildings so shafts read as wall-mounted plumbing |
| parent − 1 | Power port stubs (`PortStubVisuals` child SR, one below the parent building). Also: flywheel wheel — rendered behind the housing so the spokes peek through. |
| 10 | Buildings (depth-0 structures) |
| 11 | Tile overlays (grass on dirt, future moss on stone). Sits one above buildings so grass tufts bevelling up out of a dirt tile read in front of building bottoms placed on or beside that tile. Roads still suppress overlay rendering on the same tile (sprite cleared in `OnTileOverlayChanged`), preserving the mutual-exclusion behaviour despite the layer difference. |
| parent + 1 | Rotating wheel children sorted in front of the base (`RotatingPart` child SR — windmill blades). Per-building: the building decides whether its wheel sorts in front or behind by setting `wsr.sortingOrder` relative to its own `sr.sortingOrder` (windmill = +1, flywheel = −1). |
| 12 | Floor items resting on a building's solid top (computed by `Inventory.ComputeFloorSortingOrder` — building +2 so wheel/blade overlays at parent+1 sit between the building and the pile) |
| 15 | Platforms (depth-1 structures); also clock hand |
| 17 | Floor items resting on a platform's solid top (computed — platform +2) |
| parent + 1 | Items in storage display (drawer stacks, crate placeholder, tank fill, bookshelf fill) — `Inventory` ctor takes `parentSortingOrder` from the owning `Building` (e.g. drawer at 10 → stacks at 11). Falls back to 30 when no parent is supplied (test fixtures only). |
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

**Floor-item sort is surface-aware.** `Inventory` (Floor type) picks its sortingOrder based on the tile directly below at (x, y−1): platform-with-`solidTop` → 17, building-with-`solidTop` → 12, anything else → 70 (surface +2, so rotating-wheel overlays at parent+1 don't collide with the pile). The pile re-sorts whenever a structure is placed/destroyed under it (via `Structure` constructor + `Destroy`) or the supporting tile's type changes (`WorldController.OnTileTypeChanged`). Helpers: `Inventory.ComputeFloorSortingOrder()`, `Inventory.RefreshFloorSortingOrder()`, and the static `Inventory.RefreshFloorAt(x, y)`.

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

### Tile body rendering (chunked meshes)

Tile bodies render through **chunked meshes**, not per-tile `SpriteRenderer`s. The world is divided into 16×16-tile chunks (7×5 = 35 chunks for a 100×80 world). For each chunk, one `MeshRenderer` per solid tile type owns a `Mesh` containing one quad per same-type tile in that chunk. With 5 solid types and 35 chunks the theoretical upper bound is ~175 mesh renderers (most far fewer in practice — chunks with no tiles of a given type are skipped, and overworld chunks have only dirt). Compare ~24k per-tile `SpriteRenderer` GameObjects in the pre-chunked layout.

[TileMeshController.cs](../Controller/TileMeshController.cs) owns the grid (`bodyLayers[chunkX, chunkY, typeIdx]`), subscribes per-tile to `cbTileTypeChanged` + `cbOverlayChanged`, marks chunks dirty on callback fire, and rebuilds dirty chunks in `LateUpdate`.

**Per-tile quad geometry.** Each tile contributes 4 vertices, 6 indices in its chunk-local mesh. Quads span 1.25 × 1.25 world units centred on `(tile.x, tile.y)` — same 20×20 footprint at PPU=16 as the pre-chunked SpriteRenderers, with the same 0.25-unit overhang into each neighbour's quad. Vertex attributes:

| Channel | Semantic |
|---|---|
| POSITION | Chunk-local XY, Z=0 |
| TEXCOORD0 | Atlas-slice UV, 0..1 |
| TEXCOORD1.x | Slice index into the type's body sprite array |
| TEXCOORD1.y | Slice index into the type's normal-map array |

**Per-type Texture2DArrays.** [TileSpriteCache.cs](../Lighting/TileSpriteCache.cs) bakes the same 20×20 sprites + 256-mask-variant normal maps as before, plus a parallel `Texture2DArray` per tile type addressed by deterministic slice indices:

| Array | Depth | Slice formula |
|---|---|---|
| Body sprite (`GetBodyArray(typeName)`) | 256 × N_variants | `cMask + 16 * trimMask + 256 * variantIdx` |
| Normal map (`GetNormalMapArray(typeName)`) | 256 × N_variants | `mask8 + 256 * variantIdx` |

The arrays are populated via `Graphics.CopyTexture` (GPU-side copy from the existing per-mask `Texture2D`s into array slices) — no CPU readback. Trim-mask sprite slices upload lazily on first request; trim=0 sprites + all 256 normals upload eagerly when the type's bundle is first allocated. See the §Chunked-mesh API block at the top of `TileSpriteCache.cs` for the public signatures (`GetBodySlice`, `GetNormalMapSlice`, `GetBodyArray`, `GetNormalMapArray`).

**Materials and shaders.** One shared `chunkedTileMaterial` runs `Custom/ChunkedTileSprite` ([ChunkedTileSprite.shader](../Lighting/ChunkedTileSprite.shader)) for the visible Universal2D / UniversalForward passes. Each chunk's `MeshRenderer` binds via `MaterialPropertyBlock`:

- `_MainTexArr` → the type's body sprite array
- `_NormalArr`  → the type's normal-map array (only sampled by the normals capture pass)
- `_SortBucket` → `sortingOrder / 255`, read by `ChunkedNormalsCapture` to write the B channel

The normals capture pass uses a separate override material — `Hidden/ChunkedNormalsCapture` ([ChunkedNormalsCapture.shader](../Lighting/ChunkedNormalsCapture.shader)) — that samples the same Tex2DArrays via the per-vertex slice indices. Chunked tiles live on a dedicated `TileChunk` Unity layer; `LightFeature.NormalsCapturePass` draws that layer with the chunked override material in its own sub-pass (see §NormalsCapturePass).

**Dirty tracking.** `OnTileTypeChanged(t)` marks chunks dirty for `t` + 8 neighbours (a type flip changes neighbours' `bodyCardinals` via the soft-edge contest). `OnTileOverlayChanged(t)` marks only `t`'s chunks (overlay bits feed into the tile's own `bodyCardinals` + `trimMask` but don't reach neighbours). Mark-then-rebuild coalesces N callbacks on the same chunk into one mesh upload per frame; bursty events (winter snow accumulation, mass mining) cost at most one rebuild per affected chunk per frame.

`MarkBodyChunkDirty` blanket-dirties **all types** at the affected chunk position rather than reading the tile's current type — a neighbour-type flip can change the win/lose contest result for *other* types in the same chunk too. Rebuild loops skip empties cheaply.

**Soft edges at different-type boundaries.** Two same-type tiles meeting at a boundary share their Main interior — the seam disappears because both bake `Main` up to the boundary and their 2-pixel overhangs clear out. Two tiles of *different* types would otherwise meet at a hard straight line: both Main interiors go opaque right up to the boundary, no border art is drawn because each thinks its neighbour is "buried".

Soft edges fix that contest. The lower-id tile (the **winner**) treats the side as if it were facing air: its body bakes the same atlas border piece it would for a real air boundary, with 2px inside its own interior and 2px overhanging into the loser's interior. The higher-id tile (the **loser**) does nothing on that side — its Main stays intact. The overhang/Main overlap is resolved by **per-type sortingOrder**: the winner draws on top.

- Solid tile types are sorted by id ascending and assigned ranks 0..k−1 in `TileMeshController.BuildTypeMaps`.
- Each chunk's body MeshRenderer has `sortingOrder = −rank`. Lowest-id solid type gets sortingOrder 0, next gets −1, and so on. With current ids (dirt=2, sand=3, limestone=20, granite=21, slate=22) the band is `[0, −4]` — comfortably above water (sortingOrder −5) and below snow (sortingOrder 2).
- Same-type tiles across all chunks share the sortingOrder so they don't fight each other — only different-type pairs get a tiebreak.

Lighting is **intentionally not affected**: the body's normal map (`nMask`) runs on raw solidity (real-solid neighbours = buried) regardless of the type contest. The winner's border-art teeth at the boundary catch flat lighting and full deep-darkening alpha, identical to a buried interior — no ambient or sky light leaks through a seam between two solid tiles. The visible jaggedness is purely a sprite-pixel choice; the normal-map bake assumes the boundary is closed.

Renumbering tile-type ids in `tilesDb.json` flips render priority — lower id always wins; keep this in mind when adding new tile types (a new sand-like surface material wants a low id; a deep stone variant wants a high one).

#### Baked tile atlases (offline precompute)

The 16-cMask × 16-trim × N-variant sprite sheets and 256-mask normal maps that `TileSpriteCache` would otherwise bake on first access are precomputed at edit time and saved as `Texture2DArray` assets under `Assets/Resources/BakedTileAtlases/`. The chunked renderer loads them via `Resources.Load<Texture2DArray>` — load cost drops from ~5s to <10ms.

| Asset | Contents | Slice formula |
|---|---|---|
| `{name}_body.asset` | Body sprites: 16 cardinal × 16 trim × N variants | `cMask + 16*trimMask + 256*variantIdx` |
| `{name}_normal.asset` | Normal maps: 256 (cardinal+diagonal) masks × N variants | `mask8 + 256*variantIdx` |
| `{name}_overlay.asset` | Overlay sprites (grass / grass_dying / grass_dead / snow): 16 cardinal × N variants | `cMask + 16*variantIdx` |

Sizes: per body type ~1.6 MB (dirt, limestone with N=3 variants) or ~800 KB (sand, granite, slate with N=1). Overlay atlases ~50 KB each. Total ~12 MB on disk, committed to git.

**Bake workflow:**
- `Tools → Bake All Tile Atlases` ([TileAtlasBaker.cs](../Editor/TileAtlasBaker.cs)) — full re-bake of every body type + overlay. Run once after pulling a fresh branch.
- [TileAtlasBakeOnImport.cs](../Editor/TileAtlasBakeOnImport.cs) — `AssetPostprocessor` that watches `Resources/Sprites/Tiles/Sheets/*.png` and `Resources/Sprites/Tiles/*.png`. On any import / deletion / rename, identifies the affected atlas name (stripping trailing variant digits — `dirt2.png` → `dirt`) and re-bakes only that one. Logs `[TileAtlasBaker] Rebaked …` to console so the artist can confirm the bake ran.

**Editor-mode caveat:** the baker needs `Db.tileTypes` populated to enumerate names, but `Db.Awake` only fires in play mode. `TileAtlasBaker.EnsureDbLoaded()` calls `Db.LoadAll()` (public method extracted from `Awake`) on the scene's existing `Db` component, populating statics without entering play mode.

**Fallback contract:** if an expected `.asset` is missing (e.g. a new tile type whose bake hasn't run yet, or an `.asset` deleted from disk to force a refresh), `TileSpriteCache.EnsureTypeArrayBundle` / `EnsureOverlayArrayBundle` falls through to the runtime live bake. Functional but slow (~5s for the first type to load on miss). The fallback exists so dev iteration is never blocked — artist adds a tile type, hits Play, runtime bakes once, postprocessor catches up on next PNG save.

**Why `SetPixels32 + Apply` for the editor bake (not `Graphics.CopyTexture`):** `AssetDatabase.CreateAsset` serializes the CPU-side pixel data of a `Texture2DArray`. `Graphics.CopyTexture` writes are GPU-only and don't persist through serialization — the resulting `.asset` would load with empty slices on next domain reload. The editor bake path explicitly uses the slower `SetPixels32` route for this reason. The runtime fallback continues to use `Graphics.CopyTexture` because runtime arrays are throwaway.

### Tile overlays (grass, moss, …)

Per-side decoration rendered on top of the tile body. Today this is grass on dirt; the system generalises to moss on stone, snow, etc. Rendered through the **chunked tile renderer** (see §Tile body rendering): one `MeshRenderer` per (chunk × tile-type × overlay state), with the overlay's `Texture2DArray` bound as `_MainTexArr` and the body type's normal-map array as `_NormalArr` so body and overlay sample the same dirt-derived bevels. Sits at `sortingOrder = 11` — one above buildings so U-side grass tufts read in front of building bodies placed on or beside the tile.

- **Atlas selection**: each `TileType` declares an optional `overlay` string (e.g. dirt → `"grass"`). The atlas lives at `Resources/Sprites/Tiles/Sheets/<overlay>.png` and uses the standard 32×32 9-piece layout. Edge/corner pieces hold the decoration art; the **Main 16×16 region is ignored** — `TileSpriteCache.GetOverlay` zeros it at bake time so any "buried" side reads as transparent regardless of what the artist authored there. (Without this, stray opaque Main pixels overwrite the body's bevelled edge piece on non-decorated sides — e.g. a flat dirt-brown band along the underside of a tile that only has side grass.)
- **Per-tile state**: `Tile.overlayMask` is a 4-bit bitmask, layout `0=L 1=R 2=D 3=U` (matches the cMask convention used by the tile-sprite baker). Bit set = "this side is decorated."
- **Effective mask**: `TileMeshController.BuildOverlayGeometry` AND-masks `overlayMask` with `~cMask` so a side only shows decoration while its neighbour is non-solid. Sides that get visually buried hide their grass without touching data. Tiles where `effective == 0` emit no quad at all.
- **Map edges read as solid**: `IsSolidAt` returns `true` for off-map coordinates, so an edge tile's outward-facing cMask bit is set and its overlay/bevel/edge-darkening is suppressed on that side — the world boundary reads as "more of the same material continues." This applies uniformly to body cMask, body nMask diagonals, and the snow nMask. Gameplay queries that need actual physical solidity (e.g. snow's "is the tile above blocking sky?" visibility check) bounds-check explicitly instead of calling `IsSolidAt`. (The helper is duplicated between `TileMeshController` and any other rendering code that still consults adjacency — same off-map-as-solid convention.)
- **Inverted-mask trick**: the renderer feeds `~effective & 0xF` into `TileSpriteCache.GetOverlaySlice(overlayName, mask, x, y)`. Because the baker reads "bit set ⇒ neighbour solid ⇒ use Main interior" and overlay bakes force Main transparent, that produces edge art exactly on the desired sides. `GetOverlay`-family methods share the atlas-pixel-bake path with `Get` — only the Main fill and the 256-entry normal-map bake are skipped (overlay quads sample the body type's normal-map array, never their own).
- **Replace, don't stack**: the body's sprite uses an *augmented* cMask `bodyCardinals = realCMask | overlayBits`, so sides with grass are treated as "buried" — the body draws Main interior there, no jagged dirt edge underneath the overlay. Without this, `NormalsCapturePass`'s `Blend Off` would let grass-blade pixels overwrite the body's directional edge bevel with the flat-blade-interior bevel that `BakeNormalMap`'s Sobel kernel inevitably produces from thin grass-blade silhouettes (visible in the frame debugger as a "straight break with no slope" along the grassed edge). When a road occupies the tile, `overlayBits = 0` so the body restores its real edges.
- **Trim outer 2 inner-pixels of Main on overlay-replaced sides**: the body's Main interior extends through cols 2-17 / rows 2-17, so on a side where Main has replaced an edge piece, the silhouette would be a straight square at col 17 / row 17 — visible through transparent gaps in the overlay's edge art. `TileSpriteCache.Get(name, cMask, trimMask, x, y)` (overload, lazy-bakes per-trim-mask variants) clears the inner 2 pixels on each trim side. The renderer feeds `trimMask = overlayBits & ~realCMask` — only sides where the overlay covers a *naturally exposed* edge get trimmed; sides buried by a real solid neighbour keep Main extended for inter-tile continuity.
- **Normal map (shared)**: body and overlay chunk renderers bind the **same** `Texture2DArray` as `_NormalArr` (the body type's normal-map array). The per-vertex normal slice index uses the same `mask8` keying as the body — `TileSpriteCache.GetNormalMapSlice(tileTypeName, realNMask, x, y)`. Whether the body's Main pixel or the overlay's grass-blade pixel wins NormalsCapture at a given location, the captured normal is the same dirt-derived directional bevel. Grass blades inherit the directional edge lighting a bare dirt edge would have had.
- **Worldgen seeding**: `WorldGen.PopulateOverlays` runs after `FillDepressions` and seeds bits on every cardinal edge whose neighbour is non-solid AND non-flooded.
- **Mining never auto-sets bits**: freshly exposed sides stay bare. The `Tile.type` setter clears `overlayMask` when transitioning to a type with no overlay (e.g. dirt → empty), so mined tiles don't carry stale data.
- **Road suppression**: when `tile.structs[3] != null`, the tile emits no overlay quad. `Structure` ctor/`Destroy` call `Tile.NotifyOverlayDirty()` which fires `cbOverlayChanged` and dirties the affected chunk.
- **Live growth + health state** (`OverlayGrowthSystem`): once per real-time second, dirt tiles with `moisture > 40` (when `temperature > 5°C`) roll a small chance to sprout grass on each non-grassy, exposed, non-flooded L/R/U side (~½ in-game day expected wait per side). Bottom never grows. The same Tick also evolves a per-tile `Tile.overlayState` (Live / Dying / Dead) — death is a per-tick roll while conditions warrant it (cold/dryout → Dying, deep freeze → Dead, ~10 s steady-state average), recovery to Live is the slower fresh-grass roll. The chunked renderer maintains **one mesh per overlay state** per (chunk × tile-type): tiles in different states emit quads into different meshes, each bound to its state's atlas array (`grass` / `grass_dying` / `grass_dead`). Atlas geometry is identical across variants so per-side bit semantics are unchanged. See SPEC-systems "Soil Moisture" for the dispatch slot and full state-machine table.

### Snow cover

Snow is rendered through the **chunked tile renderer** as a separate layer at sortingOrder 2 — orthogonal to the grass overlay rather than another `tile.type.overlay` value. Reasons: grass is intrinsic to a tile type (dirt has it, stone doesn't); snow lands on any solid tile and is weather-driven, not authored. Coexistence beats reuse here. One `MeshRenderer` per (chunk × tile-type), with the shared `snow` `Texture2DArray` bound as `_MainTexArr` and the body type's normal-map array as `_NormalArr`.

- **Per-tile state**: a single `bool Tile.snow`. Driven by `SnowAccumulationSystem` (see SPEC-systems "Snow accumulation"). Cleared in the `Tile.type` setter when a snowy tile is mined — same pattern as `_overlayMask`.
- **Visibility**: hidden if the tile directly above is solid (a wall built over a snowy tile carries the data but doesn't render). No road/building gating here — sortingOrder layering handles the visuals. Tiles failing this check emit no quad.
- **Sprite**: same `TileSpriteCache.GetOverlay` cardinal-mask atlas pipeline as grass — `Resources/Sprites/Tiles/Sheets/snow.png` is a 32×32 atlas, and the renderer always asks for the U-only inverted-cardinal variant (`0b0111`), so the artist authors that one slot for "snow on top of tile". Atlas connectivity matters even with a single decorated side: corner/edge variants ensure snow reads continuously across neighbouring snowy tiles when authored that way.
- **Stacks, doesn't replace**: critical departure from grass. The body's `bodyCardinals` is **not** augmented with the snow's U bit, so the body keeps drawing its real top-edge bevel piece. The snow quad stacks on top at sortingOrder 2 — so the artist authors snow.png with transparency / vertical positioning that lets the body's bevel still read through (e.g. drawing snow in the upper region of the U-edge slot, or with semi-transparent flake pixels). This is a deliberate visual choice: unlike grass-on-dirt where the dirt edge has no business showing through, snow-on-anything wants to feel deposited on top of the tile, not built into it.
- **Normal map**: matches the body's normal data — the snow chunk's `_NormalArr` is the body type's normal-map array, addressed by the same `mask8` keying as the body. Edge bevels don't pick up the snow sprite's silhouette gradients.
- **Coexistence with grass**: accumulation **snapshots** the live `overlayMask` and `overlayState` into `tile.preSnowOverlayMask`/`State` and clears the live mask so snow renders cleanly on top. `OverlayGrowthSystem` skips snowed tiles, so the snapshot doesn't drift while snow sits there. On melt, the snapshot is restored verbatim — same grass returns. (Earlier the system killed the grass outright; preservation feels closer to real-world snow insulation and saves the player from losing established grass cover every winter.)

### Blueprint visuals

Each `Blueprint` renders a main sprite (order 100, lit, half-alpha ghost) plus a child **frame overlay** GameObject (order 101, **Unlit** layer, sliced to the footprint). The frame sprite swaps between `Sprites/Misc/blueprintframe` (blue — construct/supply) and `Sprites/Misc/bpdeconstructframe` (red — deconstruct). Frame alpha drops to `0.5` when `disabled || IsSuspended()`. The unlit layer keeps frames visible at night without participating in the lighting pipeline (same pattern as Plant's harvest overlay).

Deconstruct blueprints **hide their own main sprite** and instead apply a multiplicative tint (`DeconstructStructureTint`, currently `(1, 0.5, 0.5)`) to the underlying structure's `sr.color`. Because we only touch `sr.color` and never `sr.sprite`, live sprite changes (plant growth stages, harvest cycles, variant swaps) render correctly through the tint. `Blueprint.Destroy()` restores the structure to `Color.white` on cancel (skipped during `WorldController.isClearing` since structures are being torn down anyway).

---

## Lighting

Custom `ScriptableRendererFeature` pipeline — no URP Light2Ds used. Final result is Multiply-blitted onto the scene.

### URP setup

The project uses URP's **Universal (forward) Renderer**, *not* the 2D Renderer. The 2D Renderer ran a built-in normals-capture + 2D-lights pass (~190 draws per frame on the Main camera) that did nothing for us — we have our own `LightFeature` doing all lighting via a custom multiply blit. Switching renderers eliminated that overhead.

Two conventions flow from this choice:

**1. Dual-pass sprite shaders.** Every custom sprite shader has both `LightMode=Universal2D` AND `LightMode=UniversalForward` passes (with identical bodies), plus `Fallback "Sprites/Default"`:
- The **Universal2D** pass exists so `NormalsCapturePass.Execute` (which filters via `ShaderTagId("Universal2D")`) can find these sprites and capture their normals into `_CapturedNormalsRT`.
- The **UniversalForward** pass is what URP's Universal Renderer's transparent queue actually invokes to draw the sprite to screen.

Affected files: [ChunkedTileSprite.shader](../Lighting/ChunkedTileSprite.shader), [BackgroundTile.shader](../Lighting/BackgroundTile.shader), [Water.shader](../Lighting/Water.shader), [Sprite.shader](../Lighting/Sprite.shader), [CrackedSprite.shader](../Resources/Shaders/CrackedSprite.shader). When adding any new lit sprite shader, follow this pattern and wrap material props in `CBUFFER_START(UnityPerMaterial) ... CBUFFER_END` so SRP Batcher can batch consecutive draws.

**2. Sprite material default.** URP's Universal Renderer has no per-renderer "default sprite material" slot, and the legacy `Sprites/Default` shader has no `Universal2D` pass — sprites using it would render but be invisible to NormalsCapture (rendering at constant deep-ambient with no torch/sun lighting). To avoid that, every runtime-created lit `SpriteRenderer` is routed through:

```csharp
SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(go);
```

(in [LightReceiver.cs](../Lighting/LightReceiver.cs)) — assigns `Resources/Materials/Sprite.mat` (the dual-pass `Custom/Sprite` shader). When adding a new `AddComponent<SpriteRenderer>()` site for a lit sprite, use this helper. **Exception**: explicitly *unlit* overlays (blueprint frames, plant harvest overlays, tile highlights) keep their explicit `Sprite-Unlit-Default` assignment — they should NOT participate in NormalsCapture.

**3. Camera stacking.** Universal Renderer requires explicit camera stacking — multiple Base cameras with depth-based "Don't Clear" stacking (which the 2D Renderer tolerated) does not work; each Base camera does its own FinalBlit and the latest one overwrites. See the stack layout below.

**4. Transparency sort axis.** `WorldController.Start` sets `GraphicsSettings.transparencySortMode = CustomAxis` with axis `(0, 1, 0)`. The 2D Renderer set this on its own asset; the Universal Renderer asset has no equivalent field, and URP hides the project-level Graphics setting from the Inspector when an SRP is active — so we set it from code at startup. Almost every sprite has an explicit `sortingOrder` so this is mostly belt-and-braces, but it prevents undefined draw order between sprites sharing a sortingOrder.

### Render pipeline (per frame)

Three passes: **NormalsCapturePass** captures sprite normals + lighting tier into a temp RT; **LightPass** writes the light map (ambient + point lights + sun); **Composite** multiplies the light map onto the scene.

**LightFeature skips cameras** where `cullingMask == 0` or where `(cullingMask & (litLayers | directionalOnlyLayers | waterLayer | backgroundLayer | tileChunkLayer)) == 0` — cameras that see no sprites participating in the normals RT. The `UnlitOverlayCamera` (see §Sky / background) hits this check because the Unlit layer is excluded from all five masks.

#### NormalsCapturePass (BeforeRenderingTransparents)

Draws sprites with `Hidden/NormalsCapture` override into `_CapturedNormalsRT` (format **ARGB32** — must have alpha; camera's default HDR format has none).

**Channel packing:**

| Channel | Encodes | Notes |
|---|---|---|
| R, G | World-space `normal.x`, `normal.y` packed 0–1 | `normal.z` reconstructed in light shaders as `z = −sqrt(1 − x² − y²)`. Assumes camera-facing sprite normals (all project sprites satisfy). |
| B | Receiver sort bucket (`sortingOrder / 255`) | Smuggled per-sprite via MPB by `LightReceiverUtil.SetSortBucket`. Drives sort-aware lighting (see below). |
| A | Lighting tier + edge depth | See alpha-tier table. |

**Alpha tiers:**

| Range | Meaning |
|---|---|
| 0.80–1.0 | Solid tile. Range encodes edge depth for underground darkening — 1.0 = at exposed surface (fully lit), 0.80 = deep interior (darkened to `deepFloor`). Extracted in `LightComposite` as `saturate((alpha − 0.80) / 0.20)`. |
| 0.5 | Lit-only (full light). |
| 0.3 | Directional-only (sun + ambient only; `LightCircle` skips torch for these pixels). |
| 0.0 | No sprite (flat-normal fallback). |

**Tile border clipping**: tiles use pre-baked 20×20 sprites whose alpha already encodes the border shape. NormalsCapture clips on `_MainTex` alpha for both tiles and non-tiles — no per-pixel atlas lookup needed.

**⚠️ The native-UV invariant**: NormalsCapture's override materials sample `_MainTex` at the sprite's **native (0..1) UVs** — they have no knowledge of any custom UV math the visible shader does (world-coord sampling, parallax remap, UV scroll, vertex displacement, etc.). If your visible pass transforms UVs but NormalsCapture sees the untransformed mapping, the alpha mask captured into `_CapturedNormalsRT` won't match the alpha mask rendered to screen. The composite step then renders ghost silhouettes shaped like the *un-transformed* alpha wherever `lightmap` and `skyLightColor` differ (most visibly at sunset/sunrise; invisible at noon when the two converge).
- **Symptom**: a sprite that uses world-coord UVs (e.g., a parallax background painting, a UV-scrolling banner, a tiled wall) shows ghost outlines in the surrounding sky at specific times of day. The ghost is at the position the alpha mask *would* be if there was no parallax / scroll.
- **Fix**: bake the transformed image into a `RenderTexture` first via `Graphics.Blit` through a gen shader, then MPB-bind the RT as `_MainTex` on the sprite. The visible main pass samples the RT at native UV; NormalsCapture's override material samples the same RT at the same native UV. Masks agree.
- **Existing examples**: `CloudLayer.cs` + `Hidden/CloudFieldGen` + cloud's `mainRT`/`normalRT`. `BackgroundLayer.cs` + `Hidden/BackgroundLayerGen` + `bgRT`. Both follow the same pattern: gen shader runs per-frame in `LateUpdate`, output RT is MPB-bound as `_MainTex`.
- **Special cases the project already handles**: plant vertex sway (`NormalsCapture.shader` has dedicated logic in its frag that re-applies the same sway formula via `Sway.hlsl`), tile chunked sampling (`Hidden/ChunkedNormalsCapture` knows the per-vertex slice indices). When adding another sprite class that diverges from native-UV sampling, prefer the RT-bake pattern unless you also extend the override materials.

**Draw calls** (in order; later overwrites earlier where they overlap, `Blend Off`):

1. Background (`backgroundLayer`) — `NormalsCaptureBackground` override, alpha = 0.5 (lit-only). Drawn earliest so tiles/sprites overwrite.
2. `directionalOnlyLayers` — alpha = 0.3.
3. `litLayers & ~shadowCasterLayers & ~directionalOnlyLayers` — alpha = 0.5.
4. `tileChunkLayer` — `Hidden/ChunkedNormalsCapture` override, alpha = `lerp(0.80, 1.0, _NormalArr.a)` (shadow-caster tier). Samples per-renderer Tex2DArrays via per-vertex slice indices instead of MPB-bound 2D textures. See §Tile body rendering.
5. `litLayers & shadowCasterLayers & ~directionalOnlyLayers & ~tileChunkLayer` — alpha = `lerp(0.80, 1.0, _NormalMap.a)` (standard sprite-renderer shadow casters, drawn last so animals/structures overwrite tile pixels where they overlap).
6. `waterLayer` — `NormalsCaptureWater` override, alpha = 0.5 (lit-only).

**Sort-aware lighting** (B-channel branching in `LightCircle.shader`): per-pixel `sortDelta = receiverBucket − lightBucket` decides:
- **In-front** (`sortDelta > 0`): `effectiveHeight = −lightHeight`, zero ambient floor — forward-facing interior normals go dark (z-dot with toLight flips sign) while edge normals pointing sideways toward the light's XY still get lit. Clean silhouette block without killing rim lighting.
- **Behind** (`sortDelta ≤ 0`): `effectiveHeight` ramps from `+lightHeight` (delta = 0) toward `+lightHeight × behindFarHeightFactor` as the receiver sorts further behind (smoothstep over `sortRampRange`); full `ambientNormal` floor preserved. Default `behindFarHeightFactor = 1.0` → identity (matches pre-sort-aware lighting exactly).

The RT uses `FilterMode.Point`, so the in-front/behind boundary is always sprite-aligned — the effective-height sign flip never appears inside a single sprite.

#### LightPass (AfterRenderingTransparents)

`ConfigureTarget(LightRTId)` in `OnCameraSetup` binds the temp RT (required for the clear and for `cmd.Blit` to target it correctly across all cameras).

**Ambient fill.** Two-part model: clear to `LightFeature.deepAmbientColor`, then blit `SunController.GetAmbientColor()` modulated by `_SkyExposureTex` via `LightAmbientFill.shader`. See §Sky exposure for the full model.

**Point lights** (torches, etc.): `cmd.DrawMesh` per-light quad scaled to `outerRadius × 2`, screen blend (`BlendOp Add, Blend One OneMinusSrcColor`), radial falloff × NdotL. Skips pixels where normals RT alpha is 0–0.4 (directional-only tier).

**Sun** (directional): `cmd.Blit(null, LightRTId, sunMat)`, additive blend (`BlendOp Add, Blend One One`), NdotL with `_SunDir`. Shadow ray march is **disabled** (commented out in `LightSun.shader` for performance). **Must use `cmd.Blit`, not `cmd.DrawMesh`** — DrawMesh silently fails to write to the temp RT for cameras without PixelPerfectCamera (e.g. SkyCamera); Blit handles its own fullscreen geometry and RT binding internally, bypassing the issue.

#### Composite

`cmd.Blit(lightRT, scene, LightComposite)` multiplies scene by light map (`Blend DstColor Zero`).

- **Empty sky/background pixels** (normals RT alpha < 0.25) use a precomputed `_SkyLightColor` (sun + time-of-day ambient, no sky-exposure modulation, no point lights), blended via `skyLightBlend` (default 1.0). Base color comes from `SkyCamera.backgroundColor`.
- **Underground darkening**: solid-tile pixels (alpha > 0.75) are scaled by `lerp(deepFloor, 1.0, edgeDepth)` — deep tile interiors dimmed to `deepFloor` (default 0.2). After dimming, a `max(light, deepAmbient)` clamp ensures tile interiors never go below the universal deep ambient.

### Sky / background

Three cameras render as a URP **Camera Stack**: SkyCamera is the Base, Main and UnlitOverlay are stacked Overlays in that order. Stack ordering replaces the Camera component's `Depth` field — Depth values are ignored on stacked cameras.

| Camera | Render Type | Clear Flags | Culling Mask | Notes |
|--------|-------------|-------------|--------------|-------|
| `SkyCamera` | **Base** (stack: [Main, UnlitOverlay]) | Solid Color | Sky layer | `backgroundColor` set to raw `SunController.skyColor` each frame — fallback for any pixel `SkyGradient` doesn't cover. Lighting pipeline applies ambient × sun via the composite multiply, so we deliberately do NOT pre-multiply ambient. |
| Main Camera | **Overlay** | n/a (Overlay shares Base RT) | Everything except Unlit | PixelPerfectCamera; lighting composite applied here |
| `UnlitOverlayCamera` | **Overlay** | n/a | Unlit only | Renders after composite — sprites on the **Unlit** layer appear at full brightness, unaffected by lighting. Has `MatchCameraZoom` component to sync `assetsPPU` from Main Camera. LightFeature pipeline is skipped for this camera entirely. |

**Unlit layer pattern**: any sprite that should always appear at full brightness (tile highlights, selection overlays, debug markers) goes on the `Unlit` layer. Keep it excluded from `litLayers`, `shadowCasterLayers`, and `directionalOnlyLayers` in the LightFeature Inspector. **Also assign `Sprite-Unlit-Default`** as the material — the project's default lit material (`Custom/Sprite`, see URP setup above) participates in NormalsCapture and is wrong for the Unlit layer. For runtime-created overlays, either instantiate a prefab that carries the material (preferred — see `Plant.CreateHarvestOverlay` / `BuildIndicator`) or cache a material via `Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")`. Do NOT route Unlit-layer sprites through `SpriteMaterialUtil.AddSpriteRenderer` — that helper assigns the lit dual-pass material.

**Sky camera ambient**: `LightPass` detects `SkyCamera` and clears the light RT to **full ambient** (skipping the `LightAmbientFill` blit). This prevents sky light spatial falloff from affecting Sky-layer sprites. Sky-layer sprites still receive sun via the directional light pass.

**Raw-colors invariant for Sky-layer sprites**: every Sky-layer sprite (gradient, stars, clouds) sets `sr.color` to **raw zenith / horizon stops with NO `× GetAmbientColor()`**. The lighting pipeline applies ambient + sun via the composite multiply (`final = spriteColor × lightRT`, where `lightRT` is cleared to full ambient on SkyCamera and the sun pass adds sun NdotL). Pre-multiplying ambient on the CPU side double-darkens at night and is a bug. The `SkyCamera.backgroundColor` fallback also follows this rule.

**Lightmap clamp in `LightComposite.shader`**: in the sprite branch (`normsAlpha ≥ 0.25`), `light.rgb` is `saturate`-clamped before the multiply. Without this, on SkyCamera at noon the cleared full-ambient + additive sun NdotL pushes channels above 1, and `skyDay × (1.2, 1.35, 1.5)` saturates per-channel at the framebuffer, crushing colored sky toward white. Clamping pre-multiply preserves hue. Project is LDR throughout, so this is a sane invariant (a "fully lit" sprite renders as its source color, no over-bright multiplier).

**Sortinglayer for Sky-layer sprites**: every Sky-layer sprite *must* sit on sortingLayer **`Background`** (the cloud SRs' authored sortingLayer). Unity orders sortingLayers `Background → Default → Water → UI` (back to front) — sprites on `Default` draw *over* sprites on `Background`. SkyGradient and StarField default `sortingLayerName = "Background"` for this reason (caught when the gradient was hiding the clouds). Within `Background`: SkyGradient = -100 (back) → StarField = -50 → clouds = 0 (front).

#### Sky color stops, gradient, and stars

Two parallel 4-stop gradients on `SunController` describe the sky as a vertical band:
- `skyDay/Twilight1/Twilight3/Night` — the **zenith** color (top of sky). Static getter: `SunController.skyColor`.
- `horizonDay/Twilight1/Twilight3/Night` — the **horizon** color (bottom of sky). Static getter: `SunController.horizonColor`.

Both arrays share the same `twilightFraction`-driven phase via the private `LerpStops` helper, matching the sun-color gradient's 4 stops (Day/EarlyDusk/Dusk/Night). Author horizon stops with warmer/lighter values at twilight so the offscreen sun reads as a horizon glow. (Twilight2 was an earlier middle stop that's been removed; the gradient now interpolates Twilight1 → Twilight3 across the middle of the transition.)

`SunController` also owns `_horizonY01` (static getter: `SunController.horizonY01`) — the viewport V at which the horizon→zenith blend completes. It's a non-time-varying authoring knob that lives on SunController so all "sky look" config (colors + horizon position) sits in one inspector panel.

[SkyGradient.cs](../Lighting/SkyGradient.cs) — child of `SkyCamera`, scene singleton (`SkyGradient.instance`). Owns its own child `SpriteRenderer` (`SkyGradientSR`) and a 1×64 RGBA32 `Texture2D` (bilinear). Each `LateUpdate`:
1. Resizes the SR to the SkyCamera frustum: `localScale = (orthoSize × 2 × aspect, orthoSize × 2 / textureHeight, 1)`, `localPosition.z = 10`. The `/textureHeight` on Y is because the sprite is created with `PPU = 1` (native world size = `(1, textureHeight)` units). Reads `bgCam.orthographicSize` and `bgCam.aspect` directly — NOT `Camera.main` — because SkyCamera's zoom is dampened independently.
2. Refills the 64 texture rows by lerping `horizonColor → skyColor` **in OkLab color space**, with the t-curve `smoothstep(0, SunController.horizonY01, v)`. OkLab is perceptually uniform — a cyan-zenith → orange-horizon gradient traces through green/yellow/orange instead of dipping through muddy RGB midpoints. Per-frame cost: 2 RGB→OkLab conversions + `textureHeight` OkLab→RGB conversions (cbrt × 3 each). Negligible at default 64.
3. **Forces `alpha = 1`** on every gradient pixel and on the sample helper's return — authored color stops sometimes carry alpha=0 (Unity color picker quirk), which would otherwise make the gradient quad transparent.
4. `tex.Apply(updateMipmaps: false)`.

Public static API: `SkyGradient.SampleAtViewportY(float v01)` returns the same OkLab-interpolated blend (alpha=1). Falls back to `SunController.skyColor` (also alpha-corrected) if the singleton isn't initialized yet. (The previous sprite-pool CloudLayer used this for per-cloud tint; the current continuous-field CloudLayer doesn't sample it per-pixel, but the helper stays available for any future Sky-layer sprite that needs the gradient colour at a given viewport height.)

**OkLab boundary**: Unity's `Color` stores sRGB-encoded values. OkLab math expects **linear sRGB**, so each boundary uses `Color.linear` / `Color.gamma` to convert. The private `RgbToOklab` / `OklabToRgb` helpers in SkyGradient.cs take/return linear values; callers do the conversion outside. Out-of-gamut linear results (rare but possible for extrapolated stops) are clamped to [0,1] before the gamma encode.

`SkyGradient` uses `SpriteMaterialUtil.AddSpriteRenderer` so the gradient quad participates in NormalsCapture (alpha = 0.3 via `directionalOnlyLayers`) and gets ambient + sun multiplied at composite time. `sortingLayerName = "Background"`, `sortingOrder = -100`.

**Sprite PPU pitfall**: `Sprite.Create(tex, rect, pivot, pixelsPerUnit)` with a 1-pixel-wide texture and `PPU = textureHeight` produces a native world size of `(1/textureHeight, 1)` — bilinear can't widen a 1-pixel column, so a `localScale.x = w` only stretches the result to `w / textureHeight` units (visible as a thin vertical strip down the centre of the frustum). Use `PPU = 1` so native size is `(1, textureHeight)` and divide `localScale.y` by `textureHeight` to land at exactly `(w, h)`.

[StarField.cs](../Lighting/StarField.cs) — child of `SkyCamera`. Spawns N (default 100) child SR GameObjects at deterministic-random positions inside a **unit disk** (rejection-sampled from `[-1, 1]²`). Each frame, `unitDiskPos` is scaled by `maxR = sqrt(halfW² + halfH²)` — the frustum's half-diagonal — so the rotated disk always covers the screen rectangle. A naive rect-shaped spawn region exposes empty triangles at the screen corners on every quarter-turn; a disk inscribing those corners eliminates that. Each star reuses a single shared 1×1 white sprite. Stars are pinned to the camera frustum, never moving on screen as the player pans (terrain on the Main camera occludes whatever stars overlap with it, naturally hiding stars "below the horizon").

Per-frame tint: `sr.color = (1, 1, 1, alpha)` where `alpha = nightFactor × twinkle`:
- `nightFactor`: smoothstep ramp gated on `SunController.twilightFraction`. Tunable `nightThreshold` (default 0.1) and `rampWidth` (default 0.05) — `nightFactor = 0` for `twilightFraction ≥ threshold + ramp` (i.e. day / dusk), ramps to `1` as `twilightFraction` drops past `threshold` (deep night). Stars only appear in the deepest 15% of the day cycle by default. **Don't use `Mathf.SmoothStep(from, to, t)`** — its signature returns a value between `from` and `to`, NOT a 0–1 weight; build the smoothstep manually.
- `twinkle`: `0.6 + 0.4 × sin(time × twinkleSpeed + phase)` per-star phase offset.

Whole-field rotation: `rotationRad` accumulates `rotationSpeed × dt` (degrees/sec, default 1.5°/s ≈ 4 min per full rotation), and each star's local position is rotated around `(0, 0)` (screen centre) by hand each frame. Rotating the StarField transform directly would be cancelled by the next frame's `localPosition` re-assignment, so the rotation matrix is applied to the computed positions instead.

`sortingLayerName = "Background"`, `sortingOrder = -50` (between gradient at -100 and clouds at 0 on the same sortinglayer).

Visibility math: at full night, ambient ≈ dark blue. Star (white) × lightRT ≈ ambient (dim but ~10× brighter than the multiply-darkened sky). If they ever read too dim during play, escalate to a custom emissive shader or move stars to the Unlit layer.

### Cloud system

[CloudLayer.cs](../Lighting/CloudLayer.cs) — single SpriteRenderer (`CloudFieldSprite`) covering the camera viewport. Its `_MainTex` and `_NormalMap` are generated each `LateUpdate` on the GPU via two `Graphics.Blit` calls through [Hidden/CloudFieldGen](../Resources/Shaders/CloudFieldGen.shader). The cloud body is a constellation of CPU-spawned 3D sphere-(ellipsoid-)blobs placed at noise-density local maxima; the gen shader iterates the blobs per pixel, weighted-averages their sphere normals, and quantizes the result into 3 discrete colour bands.

**Why blobs over raw noise-gradient shading**: an early iteration used the 2D noise gradient directly as a surface normal. The noise gradient keeps varying past the silhouette, producing irregular dark patches in the middle of each cloud that didn't read as "back side of a 3D blob." Sphere-blob shading gives every pixel an unambiguous 3D normal (it's literally a sphere's surface normal), so lit/shadow transitions sweep cleanly around each visible lobe.

**Blob generation** (CPU, in `LateUpdate`):
1. Walk a regular grid **anchored in noise-space** (cell index = `floor((spriteLocal + noiseOffset) / cellSize)`) — so a given cell maps to a fixed point in the noise field regardless of camera position. Without this anchoring, blobs would pop in/out at viewport edges as the camera scrolls.
2. At each cell, compute a hash-jittered position inside the cell (deterministic per cell — blobs are stable frame-to-frame for a given noise state).
3. Sample 2D value noise at that position using the **same formula as `Noise.hlsl::ValueNoise`** — `Frac`/`Hash2D`/`ValueNoise` in [CloudLayer.cs](../Lighting/CloudLayer.cs) are a direct port. The two MUST stay in sync.
4. Noise is sampled with `anchorX / noiseAspect, anchorY` (horizontal stretch via `noiseAspect`) and gated by a quadratic band envelope with full `bandHalfHeight` above `bandCenterY` but only `bandHalfHeight × bandBottomScale` below — flat-bottom cumulus effect.
5. If density exceeds `Lerp(thresholdClear, thresholdStorm, humidity)`, emit a **parent blob** with hash-jittered radius and z. Then spawn `subBlobCount` **sub-blobs** around it (cluster of smaller children at polar offsets within `subBlobSpread × parent.radius`) for fractal-cumulus detail (big puffs + cauliflower bumps).
6. Density excess below `fadeRange` (= 0.05 above threshold) ramps the parent radius from 0 → full — smooths popping when noise grazes the threshold (humidity drifting, near-threshold cells crossing).
7. Pack into `Vector4[512]` and push via `Material.SetVectorArray("_Blobs", ...)` + `SetInt("_BlobCount", ...)`.

CPU cost: ~50 μs/frame for typical 250-blob counts.

**Two-pass gen shader**:
- **Pass 0 → mainRT** (ARGB32 linear): for each pixel, iterate `_Blobs`. Each covering blob contributes its **ellipsoidal** surface normal (`d.x / aspect, d.y, dz`, normalized, where `aspect = blobAspect` stretches blobs horizontally) weighted by metaball influence `saturate(1 − dist²/r²)`. The normalized weighted sum is a smooth normal that blends across overlapping spheres (no max() discontinuity at junctions). Lambertian against `_CloudSunHeight` × `_SunDir.xy` (cloud-specific sun elevation, decoupled from the scene's actual `_SunHeight` so the moon-phase curvature is visible regardless of in-game time of day) → continuous `ndotl`, quantized into 3 bands. Alpha = `smoothstep(thresholdCentre − softness, thresholdCentre + softness, totalInf)` where `thresholdCentre = _EdgeThreshold + noise · _EdgeWobbleStrength` — silhouettes get organically bumpy via threshold-perturb noise (NOT 2D domain warp, which previously twisted the interior shading).
- **Pass 1 → normalRT** (ARGB32 linear): FLAT tangent normal `(0.5, 0.5, 1.0, mask)`. Decodes via `(rgb·2 − 1)` to `(0, 0, 1)` — a camera-facing normal that's the same for every cloud pixel. The point is to make NormalsCapture's `_NormalMap` sample uniform across the cloud, so LightSun contributes a constant brightness rather than per-pixel N·L variation. That keeps the 3 colour bands crisp instead of getting smeared by the lightmap's smooth Lambertian. Alpha uses the same metaball coverage so NormalsCapture's clip aligns with Pass 0's silhouette exactly.

**Why bake shading into mainRT rather than let the global lightmap do it**: with a per-pixel normal map driving LightSun, the lightmap multiply gives continuous shading across the cloud. That looked smooth and "unpixel-art-like" — discrete bands need a uniform lightmap. By flattening normalRT and doing all the shading in the gen shader, the lightmap becomes a single brightness multiplier (sun strength × ambient × day/night) that scales the bands without dissolving them.

**Sun direction**: gen shader reads `_SunDir` (global, set by `LightFeature.cs:443-444`) and uses `_CloudSunHeight` for the elevation. Decoupling cloud-sun from scene-sun lets the moon-phase terminator curvature stay visible even at sunrise/sunset when the scene's actual sun is on the horizon. `_SunDir` must be declared **outside** the `UnityPerMaterial` CBUFFER in the gen shader — Unity's SRP batcher rejects globals inside per-material cbuffers.

**Positioning + parallax**: sprite is locked horizontally to `cam.x` (parallax comes from `_NoiseOffset = cam.x · worldLockingX + windOffsetX`, so as the camera or wind moves the noise pattern scrolls within the sprite). Vertically the sprite Y is `cam.y + (bandCenterY − cam.y) · worldLockingY` (sprite position physically moves with parallax for Y; same parameterization as horizontal but different mechanism since there's no `noiseOffset.y`). `worldLockingX/Y = 0` = sky-locked (no apparent motion), `= 1` = world-locked (full parallax, walk past it). Blobs are anchored to the band centre — `lyBand = anchorY − bandCenterY` is used as the blob's sprite-local y, and the whole cloud body rides along with the sprite's parallax motion.

**Binding**: both RTs are bound per-renderer via `MaterialPropertyBlock`, overriding the auto-bound `_MainTex` and `_NormalMap`. Get-modify-set pattern so any other auto-bound properties survive. Underlying sprite is a dummy 256×128 `Texture2D` whose contents are never read — it exists only to give `Sprite.Create` valid mesh dimensions.

**Linear RTs are critical**: `sRGB=false` on both. An sRGB normalRT would silently warp the `(rgb·2 − 1)` decode in `NormalsCapture.shader`.

**Knobs** (inspector on the `CloudLayer` GameObject — many parameters because the cloud system carries a lot of artistic dimensions):
- *Shape*: `textureSize` (256×128), `pixelsPerUnit` (8), `noiseScale` (0.15), `noiseAspect` (horizontal feature stretch, default 2.0).
- *Band*: `bandCenterY`, `bandHalfHeight`, `bandBottomScale` (compresses the bottom half of the envelope → flat cumulus base).
- *Coverage*: `thresholdClear` / `thresholdStorm` mapped to humidity.
- *Drift*: `windDriftScale`, `worldLockingX`, `worldLockingY`.
- *Tint*: `baseColorClear` / `baseColorStorm` (sr.color, multiplies all bands).
- *Shading bands*: `litColor` / `midColor` / `shadowColor`, `litBand` / `shadowBand` thresholds, `cloudSunHeight` (cloud-specific sun elevation for moon-phase shading curvature).
- *Sphere-blobs*: `blobCellSize`, `blobJitter`, `blobRadiusMin`/`blobRadiusMax`, `blobDepthRange`, `blobAspect` (ellipsoidal horizontal stretch per blob).
- *Sub-blobs (fractal detail)*: `subBlobCount`, `subBlobRadiusFactor`, `subBlobSpread`.
- *Edge wobble*: `edgeWobbleStrength` (perturbs the metaball alpha threshold), `edgeWobbleScale` (noise frequency).
- *Edge silhouette*: `edgeThreshold` (centre of metaball alpha smoothstep), `edgeSoftness` (half-width = fuzziness).
- *Render*: `renderZ`.

**Trade-offs worth knowing**:
- **Cloud silhouette is a union of metaball-influenced ellipsoids**. Modern look is lumpy / multi-lobed with smooth blob blending — intended trade for "looks like 3D balls glued together."
- **No cross-blob shadows**. Each sphere's own N·L darkens its sun-opposite side, but a front blob doesn't cast shadow on a back blob.
- **Point lights can't illuminate clouds** — the flat normalRT makes LightSun uniform. If we ever want a torch glow on the underside, the gen shader would need direct light source access.
- **`Hash2D`/`ValueNoise` in CloudLayer.cs MUST stay in sync with `Noise.hlsl`** — CPU spawn positions and shader noise sampling rely on identical output.

**Future shading hooks**:
- Cross-blob shadows: small extra march toward sun inside the blob loop, marking the picked normal as occluded if another blob's surface is between this pixel and the sun.
- Rim translucency / forward scatter: pixels near the front-most blob's silhouette get a 4th colour band on the sun-facing side.
- Multi-level fractal: sub-blobs could themselves spawn grand-sub-blobs for another octave of detail.

See [[project-cloud-field-system]] and [[project-sky-camera-quirks]] in memory for historical context.

### Background layer

[BackgroundLayer.cs](../Lighting/BackgroundLayer.cs) — single SpriteRenderer that displays a user-supplied tileable texture (e.g., distant hills painting) behind the clouds with parallax. Sits at `sortingOrder = -75` on the Background sortingLayer by default — between the sky gradient (-100) and stars (-50), behind clouds (0).

**Why this exists as a dedicated component**: the painting needs horizontal tiling (for camera pan), vertical clipping (one painting, not stacked tiles), parallax with `worldLockingX/Y` matching CloudLayer's convention, and lighting integration that doesn't ghost the un-parallaxed alpha mask into the sky.

**RT-bake pattern** (same trick as CloudLayer — see §NormalsCapturePass for the underlying reason):
1. Each `LateUpdate`, `Hidden/BackgroundLayerGen` runs via `Graphics.Blit(texture, bgRT, bgGenMat)` to bake the parallax-shifted view of the user texture into `bgRT`. The gen shader's fragment computes each RT pixel's world position from `_CameraPos + (uv − 0.5) · _ViewportSize`, applies parallax + texture-scale, samples `_MainTex`, and `discard`s for V outside [0,1] (vertical clip so the painting appears once, no stacked tiles).
2. `bgRT` is MPB-bound as `_MainTex` on the SpriteRenderer. Both the visible main pass AND the NormalsCapture override sample the same RT at native UVs — alpha masks agree, no sunset-time ghost mountains.
3. `bgRT` resizes to match `cam.pixelWidth/Height` so each RT pixel maps 1:1 to a screen pixel.

**Lighting integration**: a flat 1×1 normal map (0.5, 0.5, 1.0) is MPB-bound as `_NormalMap` so NormalsCapture sees a uniform camera-facing normal across the background. LightSun then contributes a constant brightness (background dims uniformly with day/night via the lightmap composite) without per-pixel spurious sun shading.

**Texture import**: Wrap Mode U = Repeat (for horizontal tiling). V can be anything since the gen shader `discard`s outside [0, 1]. Filter Mode = Point for pixel-art crispness.

**Knobs** (inspector on the `BackgroundLayer` GameObject):
- `texture`, `texturePixelsPerUnit` — content and world-scale.
- `bandCenterY`, `renderZ` — position.
- `worldLockingX`, `worldLockingY` — parallax (same convention as cloud: 0 = sky-locked, 1 = world-locked).
- `tint` — multiplied into the texture.
- `sortingOrder` — render order.

**Future hooks**:
- Real normal map for the background painting (currently flat). Would let the sun's actual direction tilt the mountains' shading. Drop-in: add a `Texture2D normalMap` field, MPB-bind it instead of the flat 1×1.
- Day/night colour-tint pulled from `SunController` (e.g., warm at dusk, blue at night) — currently `tint` is a static inspector value.

### Sky exposure (`SkyExposure.cs`)

`Assets/Lighting/SkyExposure.cs` — scene singleton (under Lighting), initialized by `WorldController.GenerateDefault()` and `SaveSystem.Load()`.

**Two-part ambient model**: ambient light = deep ambient (constant) + sky light (time-varying, distance falloff).
- **Deep ambient** = `LightFeature.deepAmbientColor` — constant color, clears the light RT. Always present, even deep underground. Not affected by day/night cycle.
- **Sky light** = `GetAmbientColor()` — added via `LightAmbientFill.shader`, modulated by `_SkyExposureTex`. Emitted from sky-exposed tiles (`!hasBackground`), falls off with Chebyshev distance into surrounding material. Falloff depth = `LightFeature.penetrationDepth` (shared with the sub-tile edge-depth baked into tile normal maps by `TileSpriteCache`). Changes with day/night cycle. Sun is also modulated by `_SkyExposureTex` in `LightSun.shader`.

**Sky exposure texture** (`_SkyExposureTex`): R8, nx×ny, **Bilinear filtered** for smooth sub-tile gradients. Set as global shader texture. Built via multi-source BFS flood fill from sky-exposed tiles — distance mapped through smoothstep to 0–255. Falloff range is `penetrationDepth + 1` so that `penetrationDepth = 1` means "1 tile of visible reach beyond the source" (the +1 offset prevents the first neighbor from getting zero exposure). Rebuilt when any tile's background wall changes.

**Edge-depth blending** (`LightComposite`): shadow-caster pixels blend toward `deepAmbientColor` based on edge depth: `lerp(deepAmbient, light, edgeDepth)`. Deep tile interiors are exactly `deepAmbientColor` everywhere regardless of sky/sun contribution.

### Background tile (`BackgroundTile.cs`)

`Assets/Lighting/BackgroundTile.cs` — scene singleton (under Lighting), initialized by `WorldController.GenerateDefault()` and `SaveSystem.Load()`.

#### Wall placement

Each `Tile` carries a `BackgroundType backgroundType` field (`None` / `Stone` / `Dirt`) with `cbBackgroundChanged` callback; `hasBackground` is a derived getter (`backgroundType != None`) for callers that just want presence (e.g. `SkyExposure`).

Placement is contour-based: `WorldGen.SetBackgrounds` (called from `WorldController.GenerateDefault` after caves are carved) puts a wall behind every tile below the natural surface heightmap, with three refinements:

1. **Near-surface skylight rule**: a non-solid tile within 2 of the surface stays `None`, so shallow caves visibly punch through to sky.
2. **1-tile sky/cave erosion**: a second pass clears any wall whose cardinal neighbour is `None`, so the topmost solid row and cave edges don't read darker than the air around them (a wall sitting one tile behind the surface dims the surface visibly).
3. **Wall type is positional**: top `WorldGen.DirtDepth` rows below surface get `Dirt`, deeper get `Stone` (matches what each tile was at fill time — stone-vein passes only convert limestone). The type is fixed at world-gen and never changes when the tile is mined or replaced.

Saved per-tile in `TileSaveData.backgroundWallType` (int enum); the legacy `hasBackgroundWall` bool is read for migration of pre-typed saves (treated as Stone) but no longer written. **`WorldController.ClearWorld` resets `backgroundType` to `None` alongside `tile.type = empty`** — without this, walls from a previous world's higher surface survive into the next load (saves only persist tiles with content).

#### Sprite & shader

A world-spanning sprite on the **Background layer** at sorting order −10 (behind tiles at 0). Uses `BackgroundTile.shader` (dual-pass `Universal2D` + `UniversalForward`), masked by a low-res RGBA32 texture (nx × ny, 1 pixel per tile).

**Mask channel encoding:** **R** = wall type (0 = Stone, 255 = Dirt); **G** = top-row flag (255 if the tile above has no background); **A** = opaque where a wall exists.

The shader samples four tileable 16×16 textures (`_WallTex`/`_WallTopTex` for stone, `_DirtWallTex`/`_DirtWallTopTex` for dirt) and selects per-pixel: top-row vs interior on G, then stone vs dirt on R. All four tile at 1 repetition per world unit via world-space UVs.

Participates in normal lighting (sun, torches, sky light) via a dedicated `NormalsCaptureBackground` override in `NormalsCapturePass` — clips transparent pixels of the *selected* wall texture so jagged top edges read as sky in the normals RT. Wall textures are set as globals (`_BackgroundTex`, `_BackgroundTopTex`, `_BackgroundDirtTex`, `_BackgroundDirtTopTex`) by `BackgroundTile.cs` for the override shader to access. Mask + sprite rebuilt on background or tile type change via dirty flag.

### Key files

All lighting C# scripts and shaders live in `Assets/Lighting/`.

| File | Role |
|------|------|
| `LightFeature.cs` | Top-level `ScriptableRendererFeature` containing `NormalsCapturePass` + `LightPass`. All inspector tunables (layer masks, ambient/penetration/skyBlend params) live here. |
| `LightSource.cs` | Per-light component (`lightColor`, `intensity`, `outerRadius`, `lightHeight`, `isDirectional`, `sunModulated`, `sortOrderOverride`); registers in a static list read by `LightPass`. `sunModulated = true` makes this light pull `SunController.torchFactor` each `Update()` and scale its intensity by it (fades torches on at dusk / off at dawn); `sortOrderOverride = -1` (default) auto-reads sortingOrder from a parent SR. |
| `LightReceiver.cs` | Two static utilities — `LightReceiverUtil.SetSortBucket(SR)` for B-channel MPB writes; `SpriteMaterialUtil.AddSpriteRenderer(go)` for runtime-created lit SRs. Plus a `LightReceiver` MonoBehaviour for prefabs with editor-authored sortingOrder (walks child SRs in `Start()`). |
| `SunController.cs` | Orbiting sun, sky color, `GetAmbientColor()`, `GetSunDirection()`. Sun child has a `LightSource (isDirectional=true)`. Inspector tunables for orbit, twilight timing, color gradients. |
| `NormalsCapture.shader` | Tangent→world normal transform for 2D sprites (non-tile renderers — animals, structures, plants, etc.). Per-renderer world-space tangent/bitangent so rotating sprites (flywheel wheel, windmill blades) get correctly rotated normals. Clips on `_MainTex` alpha. |
| `ChunkedNormalsCapture.shader` | Chunked-tile variant of `NormalsCapture`. Samples per-renderer `Texture2DArray` slices (`_MainTexArr` for alpha clip, `_NormalArr` for bevel + edge-depth) using slice indices carried in vertex `TEXCOORD1.xy`. Used as the override material for the `TileChunk` layer sub-pass. |
| `ChunkedTileSprite.shader` | Visible-pass shader for chunked body / overlay / snow meshes. Dual-pass (Universal2D + UniversalForward). Samples `_MainTexArr` via the vertex slice index. |
| `Sprite.shader` | Project-wide dual-pass replacement for `Sprite-Lit-Default` so runtime sprites participate in NormalsCapture under URP Universal Renderer. Material at `Resources/Materials/Sprite.mat`. |
| `CrackedSprite.shader` (`Assets/Resources/Shaders/`) | Broken-structure overlay — composites a tileable world-space crack texture on top of `_MainTex`, alpha-masked by base sprite. Swapped in via `Structure.RefreshTint()`. See SPEC-systems.md §Maintenance System. |
| `TileSpriteCache.cs` | Bakes 20×20 tile sprites + normal maps at load time from 32×32 border atlases. Multi-variant per tile type, deterministic per (x, y). Exposes `FlatNormalMap` for non-solid tiles. See §Normal maps for the bake details. |
| `LightCircle.shader` | Point light pass: radial falloff × NdotL. |
| `LightSun.shader` | Directional sun pass: fullscreen NdotL × sky exposure. Shadow ray march disabled. |
| `LightAmbientFill.shader` | Fullscreen pass: writes `skyLight × exposure` per pixel. Max blend onto deep-ambient-cleared RT. |
| `LightComposite.shader` | Multiply blit onto scene + edge-depth blending toward deepAmbient for deep tile interiors. |
| `SkyExposure.hlsl` | Shared HLSL include: `_CamWorldBounds`, `_GridSize`, `_SkyExposureTex` + `SampleSkyExposure(screenUV)`. Used by LightAmbientFill and LightSun. |
| `BackgroundTile.shader` | Tiles `_WallTex` / `_WallTopTex` (selected by mask green channel) at world-space UVs, masked by `_MainTex`. |
| `NormalsCaptureBgTile.shader` | Normals capture override for the background (shader name `Hidden/NormalsCaptureBackground`). Samples the four wall globals, branches on mask R/G, clips transparent pixels. |
| `SkyExposure.cs` | Sky exposure texture (R8, per-tile, BFS distance falloff). See §Sky exposure. |

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
| `_BackgroundDirtTex` | Texture2D | BackgroundTile.cs | Once (init) | NormalsCaptureBackground |
| `_BackgroundDirtTopTex` | Texture2D | BackgroundTile.cs | Once (init) | NormalsCaptureBackground |

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

**Edge bleed into solid neighbours**: the same mask-build pass also writes `127` (interior water) 2 px horizontally and 1 px downward into adjacent solid tiles' pixel space. Solid tile sprites are baked 20×20 with 2 px bevels whose corners can be transparent — without the bleed, those transparent corners read as background slits along water/wall boundaries. The bleed only fires into solid neighbours (gated on `_tileIsSolid`); empty/water neighbours are non-solid and skipped.

**Shader** (`Water/WaterSurface`): one texture sample per fragment, three branches:
- `mask < 0.25` → `discard` (transparent)
- `mask > 0.75` → `_SurfaceColor` (white highlight)
- else → `lerp(_WaterColorDark, _WaterColorLight, sin(_Time.y…) * 0.4)` (shimmer)

**Layer**: the `WaterSprite` GameObject must be on the **`Water`** Unity layer, excluded from:
- `LightFeature` `litLayers` / `shadowCasterLayers` / `directionalOnlyLayers` — water is NOT handled by the standard NormalsCapture path
- `SkyCamera` culling mask (otherwise water appears in the sky)

**Lighting**: water is lit via a dedicated path. `LightFeature` has a `waterLayer` field (set to `Water` in the Inspector) which triggers a separate `DrawRenderers` call in `NormalsCapturePass` using `Hidden/NormalsCaptureWater` (pass 1, lit-only, alpha=0.5). That shader samples the global `_WaterSurfaceTex` (set each tick by `WaterController.UpdateSurfaceMask()`) for transparency, discarding pixels with no water. Outputs flat forward normals. This means water darkens at night and receives ambient light, but torch NdotL is minimal (flat normal faces away from scene).

**sortingOrder**: `-5` — behind tiles (0), above the background wall (-10). Sitting behind tiles is what makes the edge-bleed work cleanly: bleed pixels written into solid neighbours' pixel space are covered by the solid tile body except where its bevel is transparent, so the bleed only fills the gaps it's supposed to fill. Decorative water zones (fountain basins, tank fills) therefore depend on their main building sprite having the water region authored as transparent — the shimmer reads through that hole, not over the building.

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

Same Sheets/Split pattern as items. Source sheets live in `Assets/Resources/Sprites/Plants/Sheets/`, split output in `Assets/Resources/Sprites/Plants/Split/{plantName}/`. Each sheet is 64×16 (4 columns of 16×16 cells), producing `g0.png`–`g3.png` for growth stages 0–3. Multi-tile plants (trees, bamboo) use a 2-row sheet: row 0 → `b{stage}.png` (anchor / bottom tile), row 1 → `g{stage}.png` (upper tile).

`PlantSheetSplitter.cs` — **Tools → Split All Plant Sheets**. Sprite loading falls back: `g{stage}` → `g0` → `default`. Anchor falls back: `b{stage}` → `g{stage}` chain.

The splitter skips `_blobs.png` and `_trunk.png` companion sheets — those are inputs to `PlantBlobBaker`, not in-game sprites.

### Wind sway

Plants animate against wind via one of two paths:

1. **Shader vertex sway** (default; grass, bamboo, single-stalk plants). `WindShaderController` (a MonoBehaviour on the scene root) pushes wind globals into `PlantSprite.shader`; per-plant `MaterialPropertyBlock` writes (`SetPlantSwayMPB` in `LightReceiverUtil`) carry a per-instance phase + tile height. Bend amplitude grows with height-above-anchor so the trunk stays put and the canopy oscillates. Optional `_sway.png` companion sheet provides a per-pixel mask (R = sway weight, 0 = rigid); when present, `Plant.cs` flips the SR into mask-mode (currently disabled — see `HasSwayMaskCompanion`).

2. **Blob-sway** (trees). When a plant ships a `{plantName}_blobs.png` companion mask, `PlantBlobBaker` bakes the foliage into per-blob sprites + a sway metadata JSON. At runtime the plant uses non-sway lit material and spawns one child SR per blob; `PlantController.Update` translates each blob's transform every frame:

   ```
   swing = (sin((t + plantPhase) * SwaySpeed + b.phase) + 1) * 0.5    // 0..1
   dx    = swing * SwayAmplitudePx * windSigned                        // signed pixels
   blob.localPosition.x = dx / 16
   ```

   `windSigned` comes from `WeatherSystem.wind` clamped to [-1, +1] (positive = blowing right). Swing only ever rests at 0 and leans 1, so blobs sway *with* the wind — never against. Continuous (no rounding) so wind smoothly scales amplitude. `SwayAmplitudePx = 1` keeps leaves from detaching at full gust.

   **Authoring** (per plant):
   - `{plantName}_blobs.png` — same cell grid as base sheet. Each unique colour = one blob region. Pure black or white = "static blob" (covers trunk but never sways — useful for anchor leaves at the trunk).
   - `{plantName}_trunk.png` (optional) — persistent trunk silhouette visible *through* the foliage. Bake reads it as the fallback layer behind blob pixels, so when a leaf shifts off its rest position, the trunk shows through the gap instead of sky.

   **Bake** (`Tools → Bake Plant Blob Sway + Generate Normal Maps`):
   - Reads base + `_blobs` + optional `_trunk` sheets, walks the same cell grid as the splitter.
   - Per cell emits `{cellName}_static.png` (trunk + non-foliage) and `{cellName}_b{i}.png` (one sprite per blob colour, transparent outside).
   - Writes a single `sway_meta.json` per plant listing per-blob phase + isStatic for every cell (`PlantSwayMetaCache` reads + caches this at runtime).
   - Cleans orphaned outputs from prior baker layouts (`_anim`, `_f*`, stale `_b*`/`_static`/`_n` companions).
   - Existing `SpriteNormalMapGenerator` picks up the new `_static` / `_b*` PNGs automatically — each blob gets its own bevelled normal map for individual leaf shading.

   **Runtime registration**: `Plant.TryEnableBlobSway` (called in the Plant ctor) probes `PlantSwayMetaCache.Get(plantName)`; if a sway_meta exists, the plant registers with `PlantController.RegisterSwaying` and the controller's `Update()` walks the swaying-plant list each frame. Calm weather (`|wind| < 0.001`) short-circuits the entire loop after one snap-to-zero pass.

   **Gotcha — `UpdateSprite` is expensive on the blob-sway path**: it clears every child blob GO and respawns them at `localPosition = Vector3.zero`. Only call it when the plant's growth stage actually changes (see `Plant.Grow` — guards on `growthStage != prevStage`). Calling it every tick caused a synchronized one-frame snap on every blob in the scene; if you add a new caller, make sure it's truly needed.

---

### Normal maps

**Encoding**: tangent space (sprite-local), packed 0–1. Out-of-texture = local +Z. Flat camera-facing pixel = tangent `(0, 0, +1)` → packed `(0.5, 0.5, 1.0)` (= `(128, 128, 255)` byte). `NormalsCapture.shader` transforms this to world space using the renderer's own basis (`worldT`, `worldB` derived from `TransformObjectToWorldDir`), so rotating sprites get correctly rotated normals. For an unrotated, axis-aligned sprite the transform reduces to `(x, y, −z)` in world space. Black = no sprite, shader uses flat fallback. No Y-flip on screen UV (DrawRenderers and the light pass projection both use OpenGL convention, V=0 at bottom).

**Tile normal maps** (`TileSpriteCache.BakeNormalMap`): baked per-variant alongside each 20×20 sprite, driven by the sprite's own alpha + an 8-bit adjacency mask (4 cardinals + 4 diagonals). Chunked body meshes read them via per-vertex slice index into the type's normal-map `Texture2DArray`; per-tile overlay/snow `SpriteRenderer`s apply them via `MaterialPropertyBlock` (`_NormalMap`). Non-solid tiles use `TileSpriteCache.FlatNormalMap`.

*Keying scheme.* Sprites are keyed by the 4-bit cardinal mask (16 entries per variant); normal maps need the full 8-bit mask (256 entries) because diagonal openings change both the RGB bevel and the edge-depth alpha at inside corners.

*Effective opacity per pixel.* Computed before encoding RGB and alpha:
- Interior pixels → sprite alpha.
- Four 2×2 overhang corners (e.g. (0–1, 0–1) = BL) → opaque when the matching diagonal neighbour is solid (it spatially owns that corner); else sprite alpha.
- Non-corner overhang pixels (strictly one side of the interior) → opaque when the matching cardinal neighbour is solid; else sprite alpha.
- Positions outside the 20×20 → transparent (empty air).

*RGB (bevel direction).* Sobel-combined outward bevel normal — cardinal transparent neighbours contribute ±1 outward unit; each of the four diagonal transparent neighbours contributes `±BevelDiagWeight` (default 0.5) on both axes. This widens the 1-pixel bevel into a ~1.5px soft bevel: the directly-adjacent ring keeps full outward tilt, the next ring in picks up gentle diagonal tilt — so an inside-corner interior pixel (solid L + solid D + empty BL diagonal) still catches grazing light, and a dithered grass surface lights the row just below any punched-through top pixel via the cardinal term.

*Alpha (edge depth).* Euclidean distance-transform to the nearest effectively-transparent pixel, mapped through a smoothstep over `LightFeature.penetrationDepth × TILE` (16 px at default). Drives `LightComposite`'s fade toward `deepAmbient` for underground darkening. The transparent BL 2×2 overhang at an inside corner also lets the distance transform reach into the interior, adding edge-depth brightening on top of the diagonal bevel.

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

---

## Primary visual spawn — `StructureVisualBuilder`

A structure's "primary visual" — the sprite renderers that depict the building itself — is spawned in three places: the live `Structure` ctor (full opacity), the `Blueprint` ctor (translucent ghost during construction), and `MouseController` (the cursor-following placement preview in build mode). [StructureVisualBuilder.cs](../Model/Structure/StructureVisualBuilder.cs) is the single source of truth — all three sites call `Build(parent, st, shape, mirrored, rotation, baseSortingOrder, tint)`.

**Returned `Refs`**: `mainSr` (anchor SR; disabled for custom-visual types but kept non-null so downstream code reading `sr.sortingOrder` works), `extensionSrs[]` (`_b/_m/_t` vertical-shape children), `customSrs[]` (per-StructType extras like tarp's cloth + posts), and `tintableSrs[]` (flattened union — walked by `Structure.SetTint` so the deconstruct red overlay reaches every spawned SR without per-subclass code).

**Two paths**:
- **Standard** (default): resolves the anchor sprite via `StructureVisuals.ResolveAnchorSprite`, applies sliced fallback if missing, spawns vertical extension SRs for shape-aware `nx==1, ny>1` shapes (`_b/_m/_t` slice convention).
- **Custom-visual** (per-name dispatch in `Build`): handles structures whose visual doesn't fit the standard sprite-and-extensions model. Currently: `tarp` → `BuildTarp`. To add a new custom visual (tassels, banners, etc.): add one branch in `Build`, write a small static helper that spawns the children. No edits to Structure.cs / Blueprint.cs / MouseController.cs needed.

**Caller responsibilities**: each caller positions its own `parent` GO (depth-aware for built structures, same convention for blueprints, cursor-following for previews) and chooses `baseSortingOrder` for its layer (depth-derived built / 100 blueprint / 200 preview). `Build` applies rotation, sortingOrder, sort bucket, and tint uniformly; callers don't replicate that logic.

**MouseController state cache**: the preview respawns visuals only when `(structType, shapeIndex, mirrored, rotation)` changes — per-frame work is just a `transform.position` update on the parent GO, no GC. Spawned visuals live under a `previewVisualRoot` child of `buildPreview` so toggling them as a unit on Build/Remove mode transitions is one `SetActive` call.

## Tarps — stretched-sprite decoration

`Tarp` is a horizontal-only shape-aware building (`shapes` array of widths 3–6, `depth: 1`) — a thin `Building` subclass in [Tarp.cs](../Model/Structure/Tarp.cs) that exists mainly so `Structure.Create` has a hook to dispatch to. The actual visual lives in the `TarpVisuals` static helper in the same file (`Spawn` / `Layout` / `Tint` / `SetActive`), invoked by `StructureVisualBuilder.BuildTarp`.

`BuildTarp` creates a *disabled* main SR (kept so `Structure.sr` stays non-null for downstream reads — sortingOrder, deconstruct overlay) plus three custom child SRs:
- **Cloth** — `Sprites/Buildings/tarp_cloth.png`, anchored at the midpoint between posts, `drawMode = Sliced` with `size = (nx-1, 1)` so it stretches horizontally between the two post centres. Configure 9-slice borders on the asset's importer to control which pixels stay constant vs. stretch. `sortingOrder = base + 1`.
- **Left post** — `Sprites/Buildings/tarp_post.png` at the leftmost tile centre (localX=0), no flip.
- **Right post** — same sprite at the rightmost tile centre (localX=nx-1), `flipX = true` so the art reads as its own reflection.

Missing assets log a warning and that piece is skipped — the rest still renders.

`StructType.blocksRain = true` (without `solidTop`) makes tiles directly under the tarp report `IsExposedAbove == false` — see SPEC-systems §Rain/wind. Items don't rest on the cloth (no solidTop, no item-falling stop), and mice can't stand on it.
