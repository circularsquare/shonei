using UnityEngine;

// Per-light geodesic "reach" field for flood-fill point lighting (SettingsManager.floodFill).
//
// The current point-light model (LightCircle.shader) lights a radial disc and occludes with a
// straight-line thickness ray-march — so light can't bend around a corner into an L-shaped tunnel
// or a burrow door. This computes, per light, a small grid of how much of THAT light reaches each
// nearby cell when routing AROUND walls (a shortest-travelled-distance / flood fill), bounded by the
// light's radius. The shader samples it for the light's MAGNITUDE; the NdotL DIRECTION stays the
// light's real toLight vector (so normal-map shading, multi-directional faces, height, colour, and
// the sort-aware flip all keep working — see propagated-lighting.md, "per-light geodesic reach").
//
// Occlusion comes entirely from WallField's per-edge walls (terrain boundaries + burrow perimeters):
//
// Two phases:
//   1. Geodesic over OPEN cells only. Light steps cell→cell at cost 1 (orthogonal) / sqrt2 (diagonal),
//      but a wall edge HARD-BLOCKS the step — so light can't pass through a wall and instead routes
//      around it (the around-corner reach). Diagonals can't cut wall corners. Cells with no open path
//      from the light stay unreached (dark).
//   2. Light visible solid FACES. A wall's front face faces the light across open air, so it must be
//      lit — but in phase 1 it's an unreached cell behind a wall edge, hence dark. So each unreached
//      cell that borders a lit open cell ACROSS A WALL inherits that neighbour's distance (+1 tile).
//      This is the load-bearing fix for the "floor dims as it nears any wall" artifact: without it the
//      wall cell reads dark and the bilinear texture filter drags the adjacent bright floor down to it.
//      Cells deeper in solid (no lit open neighbour) stay dark, so walls still block what's behind.
//
// Geodesic distance is solved by iterative fast-sweeping relaxation over the small window (~21² cells
// → converges in a handful of sweeps). Baked to an R8 texture (bilinear, so the faceted grid contours
// smooth out) plus a world-space rect for uv mapping.
//
// Caching: static torches bake once and stay cached; EnsureBaked re-bakes only when the light moved
// to a new tile or WallField changed (version counter) — so only the moving cursor light re-bakes
// each frame, which is cheap. Called from LightSource.LateUpdate when flood-fill is on.
public static class LightReachField {
    const float Sqrt2 = 1.41421356f;

    // 8-neighbour offsets (4 orthogonal then 4 diagonal).
    static readonly int[] OX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    static readonly int[] OY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    // Scratch buffers, reused across lights (main-thread only — LateUpdate).
    static float[] dist;
    static float[] face;   // phase-2 visible-face distances (separate so faces don't chain into solid)
    static byte[]  bytes;

    // Sub-tile movement under which we don't bother re-baking (a static torch never moves, so it bakes
    // once; the cursor light re-bakes whenever it slides this far).
    const float RebakeEps = 0.02f;

    // Re-bake src.reachTex if stale; cheap no-op when already current.
    public static void EnsureBaked(LightSource src) {
        float lx = src.transform.position.x, ly = src.transform.position.y;
        int R = Mathf.Max(1, Mathf.CeilToInt(src.outerRadius));
        int W = 2 * R + 1;
        int wallVer = WallField.version;
        if (src.reachTex != null && src.reachW == W && src.reachWallVersion == wallVer
            && Mathf.Abs(lx - src.reachPosX) < RebakeEps
            && Mathf.Abs(ly - src.reachPosY) < RebakeEps) return; // still current
        Bake(src, R, W, Mathf.RoundToInt(lx), Mathf.RoundToInt(ly), lx, ly, wallVer);
    }

    static void Bake(LightSource src, int R, int W, int cx, int cy, float lx, float ly, int wallVer) {
        var wf = WallField.instance;
        int n = W * W;
        if (dist == null || dist.Length < n) { dist = new float[n]; bytes = new byte[n]; }
        for (int i = 0; i < n; i++) dist[i] = float.MaxValue;

        // Sub-tile seeding — anchor the field to the light's TRUE position (not its rounded cell) so it
        // slides smoothly and doesn't snap when the light crosses a tile boundary. Seed the containing
        // cell plus the (up to) 3 bracket cells toward the sub-tile offset with their euclidean
        // distance to the light — but a bracket cell is seeded only if the light can actually reach it
        // (passable + open edge), so light doesn't leak across a wall right at the source. (A light
        // sitting inside solid seeds only its solid cell → it lights its visible faces, not the
        // interior, via the face pass.)
        Seed(cx, cy, lx, ly, R, W, cx, cy);
        int sx = (lx >= cx) ? 1 : -1;
        int sy = (ly >= cy) ? 1 : -1;
        SeedOpen(wf, cx + sx, cy,      cx, cy, lx, ly, R, W);
        SeedOpen(wf, cx,      cy + sy, cx, cy, lx, ly, R, W);
        SeedDiag(wf, cx + sx, cy + sy, cx, cy, lx, ly, R, W);

        // Fast-sweeping: alternate raster directions until no cell improves. Window is tiny, so the
        // changed-guard breaks out after a few sweeps even for spiralling tunnels.
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 2 * W) {
            changed = false;
            for (int dir = 0; dir < 4; dir++) {
                int iStart = (dir & 1) == 0 ? 0 : W - 1;
                int iStep  = (dir & 1) == 0 ? 1 : -1;
                int iEnd   = (dir & 1) == 0 ? W : -1;
                int jStart = (dir & 2) == 0 ? 0 : W - 1;
                int jStep  = (dir & 2) == 0 ? 1 : -1;
                int jEnd   = (dir & 2) == 0 ? W : -1;
                for (int j = jStart; j != jEnd; j += jStep)
                    for (int i = iStart; i != iEnd; i += iStep)
                        changed |= Relax(wf, i, j, R, W, cx, cy);
            }
        }

        // Phase 2 — one-layer face bleed across walls. An unreached cell bordering a lit cell ACROSS a
        // wall inherits that neighbour's distance (+1 tile of falloff). Written to a separate buffer so
        // a just-lit face can't chain deeper (only the first layer past a wall lights; everything beyond
        // stays dark). Two cases this covers: (a) a SOLID cell = the lit front face of rock; (b) an OPEN
        // cell across a BURROW shell = seepage into/out of a burrow (the only open↔open wall there is).
        // So a lit burrow glows one tile into the open space around it, and an outside light faintly
        // kisses a burrow wall — symmetric, soft, and bounded to a single tile.
        if (face == null || face.Length < n) face = new float[n];
        for (int j = 0; j < W; j++)
            for (int i = 0; i < W; i++) {
                int idx = j * W + i;
                int wx = cx - R + i, wy = cy - R + j;
                if (dist[idx] != float.MaxValue) { face[idx] = dist[idx]; continue; }
                float best = float.MaxValue;
                if (i > 0)     best = InheritFace(best, dist[j * W + i - 1],       wf, wx - 1, wy, wx, wy);
                if (i < W - 1) best = InheritFace(best, dist[j * W + i + 1],       wf, wx + 1, wy, wx, wy);
                if (j > 0)     best = InheritFace(best, dist[(j - 1) * W + i],     wf, wx, wy - 1, wx, wy);
                if (j < W - 1) best = InheritFace(best, dist[(j + 1) * W + i],     wf, wx, wy + 1, wx, wy);
                face[idx] = best;
            }
        var swap = dist; dist = face; face = swap; // falloff reads the face-lit field

        // Geodesic distance → falloff, matching LightCircle's radial shape ((1 - smoothstep)²) so
        // open-space flood-fill ≈ the current radial look (clean A/B). Unreached cells (MaxValue)
        // fall to 0.
        float inner = src.innerRadius;
        float range = Mathf.Max(0.01f, src.outerRadius - inner);
        for (int k = 0; k < n; k++) {
            float t  = Mathf.Clamp01((dist[k] - inner) / range);
            float ss = t * t * (3f - 2f * t);          // smoothstep(inner, outer, gd)
            float reach = (1f - ss) * (1f - ss);
            bytes[k] = (byte)(Mathf.Clamp01(reach) * 255f);
        }

        if (src.reachTex == null || src.reachW != W) {
            if (src.reachTex != null) Object.Destroy(src.reachTex);
            src.reachTex = new Texture2D(W, W, TextureFormat.R8, false) {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp
            };
        }
        src.reachTex.SetPixelData(bytes, 0);
        src.reachTex.Apply(false);

        // uv rect: tiles are centred on integer coords, so the window's lower-left CORNER is half a
        // tile below/left of the lower-left cell centre. Shader maps (worldPos - xy) / zw → uv.
        src.reachRect = new Vector4(cx - R - 0.5f, cy - R - 0.5f, W, W);
        src.reachW = W;
        src.reachPosX = lx;
        src.reachPosY = ly;
        src.reachWallVersion = wallVer;
    }

    // True if light can travel through cell (x,y): open (non-occluder). Solid cells are islands — light
    // never propagates through them; they only show their lit faces (phase 2).
    static bool Passable(WallField wf, int x, int y) {
        return wf == null || !wf.Solid(x, y);
    }

    // Seed a window cell with the light's true euclidean distance (sub-tile anchored). Keeps the min.
    static void Seed(int wx, int wy, float lx, float ly, int R, int W, int cx, int cy) {
        int li = wx - (cx - R), lj = wy - (cy - R);
        if (li < 0 || li >= W || lj < 0 || lj >= W) return;
        float dx = lx - wx, dy = ly - wy;
        float d = Mathf.Sqrt(dx * dx + dy * dy);
        int idx = lj * W + li;
        if (d < dist[idx]) dist[idx] = d;
    }

    // Seed an orthogonal bracket cell only if the light reaches it (open edge + passable).
    static void SeedOpen(WallField wf, int tx, int ty, int fx, int fy, float lx, float ly, int R, int W) {
        if (!Passable(wf, tx, ty) || CrossesWall(wf, fx, fy, tx, ty)) return;
        Seed(tx, ty, lx, ly, R, W, fx, fy);
    }

    // Seed the diagonal bracket cell only if a wall-free L-route to it exists and it's passable.
    static void SeedDiag(WallField wf, int tx, int ty, int fx, int fy, float lx, float ly, int R, int W) {
        if (!Passable(wf, tx, ty)) return;
        bool r1 = !CrossesWall(wf, fx, fy, tx, fy) && !CrossesWall(wf, tx, fy, tx, ty);
        bool r2 = !CrossesWall(wf, fx, fy, fx, ty) && !CrossesWall(wf, fx, ty, tx, ty);
        if (r1 || r2) Seed(tx, ty, lx, ly, R, W, fx, fy);
    }

    // Relax cell (i,j) against its 8 neighbours: dist = min(dist, neighbourDist + moveCost). Solid
    // cells don't propagate (light can't pass through rock) — only passable cells route the flood.
    static bool Relax(WallField wf, int i, int j, int R, int W, int cx, int cy) {
        int wx = cx - R + i, wy = cy - R + j;
        if (!Passable(wf, wx, wy)) return false; // solid: keep its seed (if any), never relax/spread
        int idx = j * W + i;
        float cur = dist[idx];
        bool changed = false;
        for (int o = 0; o < 8; o++) {
            int ni = i + OX[o], nj = j + OY[o];
            if (ni < 0 || ni >= W || nj < 0 || nj >= W) continue;
            float nd = dist[nj * W + ni];
            if (nd == float.MaxValue) continue;
            int nwx = cx - R + ni, nwy = cy - R + nj;
            if (!Passable(wf, nwx, nwy)) continue;                  // don't spread out of a solid cell
            float c = MoveCost(wf, nwx, nwy, wx, wy);               // neighbour → this cell
            if (c >= float.MaxValue) continue;
            float cand = nd + c;
            if (cand < cur - 1e-4f) { cur = cand; changed = true; }
        }
        if (changed) dist[idx] = cur;
        return changed;
    }

    // Cost to move between adjacent OPEN cells A=(ax,ay) and B=(bx,by). A wall edge hard-blocks the
    // step (MaxValue → light routes around, not through). A diagonal is allowed only if one of its two
    // L-routes is wall-free (no cutting through a wall corner), else blocked.
    static float MoveCost(WallField wf, int ax, int ay, int bx, int by) {
        int dx = bx - ax, dy = by - ay;
        if (dy == 0) return CrossesWall(wf, ax, ay, bx, by) ? float.MaxValue : 1f; // horizontal
        if (dx == 0) return CrossesWall(wf, ax, ay, bx, by) ? float.MaxValue : 1f; // vertical
        // diagonal: route1 A→(bx,ay)→B, route2 A→(ax,by)→B
        bool r1 = !CrossesWall(wf, ax, ay, bx, ay) && !CrossesWall(wf, bx, ay, bx, by);
        bool r2 = !CrossesWall(wf, ax, ay, ax, by) && !CrossesWall(wf, ax, by, bx, by);
        return (r1 || r2) ? Sqrt2 : float.MaxValue;
    }

    // Phase-2 helper: if open neighbour at (nx,ny) is lit AND the edge to this face cell (fx,fy) is a
    // wall, the face inherits the neighbour's distance + 1 tile. Returns the running min.
    static float InheritFace(float best, float neighbourDist, WallField wf, int nx, int ny, int fx, int fy) {
        if (neighbourDist == float.MaxValue) return best;          // neighbour itself unlit
        if (!CrossesWall(wf, nx, ny, fx, fy)) return best;          // not a wall face (open→open handled in phase 1)
        return Mathf.Min(best, neighbourDist + 1f);
    }

    // Is the edge between orthogonally-adjacent cells A and B a wall (terrain OR burrow)?
    static bool CrossesWall(WallField wf, int ax, int ay, int bx, int by) {
        if (wf == null) return false;
        if (ay == by) return wf.VEdge(Mathf.Max(ax, bx), ay);   // vertical edge on line max(ax,bx)
        return wf.HEdge(ax, Mathf.Max(ay, by));                 // horizontal edge on line max(ay,by)
    }
}
