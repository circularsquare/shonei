# Shonei — Rendering & Lighting

## Rendering & Layers

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

Custom `ScriptableRendererFeature` pipeline — no URP Light2Ds used. All lights use `BlendOp Max` so overlapping sources take the brightest value. Final result is Multiply-blitted onto the scene.

### Render pipeline (per frame)

1. **NormalsCapturePass** (`BeforeRenderingTransparents`) — draws all sprites with `Hidden/NormalsCapture` override material into `_CapturedNormalsRT`. Outputs world-space normals packed 0–1 (`rgb * 0.5 + 0.5`). Transparent pixels discarded (background stays black = flat fallback).
2. **LightPass** (`AfterRenderingTransparents`) — clears light RT to `SunController.GetAmbientColor()`, then `BlendOp Max`-draws:
   - Sun (directional): fullscreen quad, NdotL with `_SunDir` from `SunController.GetSunDirection()`.
   - Point lights (torches, etc.): per-light quad scaled to `outerRadius×2`, radial falloff × NdotL with `toLight = normalize(lightXY − fragXY, −lightHeight)`.
3. **Composite** — `cmd.Blit(lightRT, scene, LightComposite)` multiplies scene by light map (`Blend DstColor Zero`).

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

### Normal maps

**Encoding**: world-space, packed 0–1. Flat camera-facing sprite = `(0,0,−1)` → `(0.5, 0.5, 0.0)`. Black = no sprite, shader uses flat fallback. No Y-flip on screen UV (DrawRenderers and the light pass projection both use OpenGL convention, V=0 at bottom).

**Tile normal maps** (`TileNormalMaps.cs`): 16 procedural variants (4-bit adjacency mask). Exposed edges bevel outward; interior stays flat. Applied via `MaterialPropertyBlock` on tile `SpriteRenderer`s.

**Sprite normal maps** (`SpriteNormalMapGenerator.cs`): editor tool (**Tools → Generate All Sprite Normal Maps**) batch-processes `Assets/Resources/Sprites/`. For each texture:
1. Generates `_n.png` — edge pixels get outward normals, interior gets flat forward normal.
2. Imports as `Default` / `Uncompressed` RGBA32 (not NormalMap type — must stay plain packed 0–1).
3. Auto-assigns as `_NormalMap` secondary texture on the source sprite importer.
