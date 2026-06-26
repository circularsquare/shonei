// Draws a soft radial gradient for one point light source, modulated by NdotL
// from _CapturedNormalsRT (world-space normals rendered by NormalsCapturePass).
// Max blend: overlapping sources take the brightest value without blowing out,
// and torches compete with (rather than stack on) ambient light.
Shader "Hidden/LightCircle" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        BlendOp Max
        Blend One One
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "LightCircle"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Set per-draw via MaterialPropertyBlock.
            float4 _LightColor;
            float  _Intensity;
            float  _InnerFraction;     // innerRadius / outerRadius  (0–1)
            float4 _LightWorldPos;     // world-space XY of the light source
            float  _LightHeight;       // Z offset above sprite plane for NdotL angle
            float  _LightSortBucket;   // this light's bucket value (SortBucketUtil scheme; see LightSource.sortBucket)
            float  _AmbientNormal;     // minimum NdotL floor (softens back-face darkness)
            float  _CenterFlatten;     // 0 = raw NdotL hot-spot; 1 = flat center that still respects normals (see LightSource.centerFlatten)
            // Global ramp params (set once per frame by LightPass).
            // _SortRampRange         = sort-delta range (in normalized bucket units) over
            //                         which the effective height ramps, on the behind side,
            //                         from +_LightHeight toward +_LightHeight*_BehindFarHeightFactor.
            // _BehindFarHeightFactor = height scale at the far end of the behind ramp.
            //                         1.0 = flat ramp (uniform behind lighting);
            //                         >1 = steeper/more top-down for deep-behind receivers;
            //                         <1 = shallower/more grazing.
            float  _SortRampRange;
            float  _BehindFarHeightFactor;

            // Wall-occlusion toggle (set per frame by LightPass from SettingsManager.pointShadows).
            // 1 = thickness-attenuate the light by solid material crossed toward the light; 0 = no occlusion.
            float  _PointShadows;

            // Flood-fill (geodesic) mode (global, set per frame from SettingsManager.floodFill).
            // 1 = take the light's MAGNITUDE from its per-light geodesic reach field (_ReachTex, baked
            // by LightReachField — already includes geodesic falloff + around-corner occlusion)
            // instead of the radial falloff + SolidThickness shadow. The NdotL DIRECTION still uses the
            // real toLight, so normal-map shading is preserved (see propagated-lighting.md).
            float  _FloodFill;
            // Per-light reach field (set via MPB). R8, WxW window centred on the light, bilinear.
            // _ReachRect = (originWorldX, originWorldY, width, height) in tiles → uv = (worldPos-xy)/zw.
            // A zero rect (width 0) means "not baked this light" → fall back to radial falloff.
            TEXTURE2D(_ReachTex);
            SAMPLER(sampler_ReachTex);
            float4 _ReachRect;

            // Populated each frame by NormalsCapturePass.
            TEXTURE2D(_CapturedNormalsRT);
            SAMPLER(sampler_CapturedNormalsRT);

            // Occluder distance field (OccluderField.cs): per-tile world-space distance (tiles) to
            // the nearest wall, bilinear. 0 inside walls, >=1 in open cells. _GridSize = (nx, ny).
            // Walked by SolidThickness for the soft thickness-attenuated shadow.
            TEXTURE2D(_OccluderDist);
            SAMPLER(sampler_OccluderDist);
            float4 _GridSize;

            // Burrow-wall edge mask (WallField.cs): (nx+1)×(ny+1) point-sampled. R = vertical burrow
            // wall on grid line x at row y; G = horizontal burrow wall on column x at grid line y. A
            // burrow is a hollow carved INSIDE solid tiles, so its perimeter (incl. a ceiling with
            // open air above) blocks light even though the cell isn't in _OccluderDist. SolidThickness
            // hard-blocks when the ray to the light crosses one of these edges. _WallTexSize=(nx+1,ny+1).
            TEXTURE2D(_WallBurrowTex);
            SAMPLER(sampler_WallBurrowTex);
            float4 _WallTexSize;

            // Accumulated thickness (in tiles) of solid material the ray from `p` to `lightPos`
            // crosses. Exact DDA grid walk (Amanatides & Woo) that sums the segment length spent
            // inside each solid cell. Because it's a continuous function of the light/receiver
            // positions, the shadow edge FADES smoothly as a grazing ray catches more/less wall —
            // unlike a binary blocked test, which snaps from lit to black at a tile boundary. It
            // visits EVERY cell the ray crosses (no leaps), so a straight shot through a 1-tile
            // wall accumulates a full tile of thickness (no thin-wall leak); only corner-grazing
            // rays accumulate a fraction, which is exactly the soft penumbra we want. Solidity is
            // read from the field (a cell centre samples ~0 inside walls, >=1 in open). Tiles are
            // centered on integer world coords, so cell indexing shifts by +0.5.
            float SolidThickness(float2 p, float2 lightPos) {
                float2 dvec = lightPos - p;
                float  dist = length(dvec);
                if (dist <= 1e-4) return 0.0;
                float2 rd     = dvec / dist;
                float2 rdSafe = float2(abs(rd.x) < 1e-6 ? 1e-6 : rd.x, abs(rd.y) < 1e-6 ? 1e-6 : rd.y);
                float2 g      = p + 0.5;
                int2   cell   = (int2)floor(g);
                int2   stp    = int2(rd.x >= 0.0 ? 1 : -1, rd.y >= 0.0 ? 1 : -1);
                float2 tDelta = abs(1.0 / rdSafe);
                float2 nb     = floor(g) + float2(rd.x >= 0.0 ? 1.0 : 0.0, rd.y >= 0.0 ? 1.0 : 0.0);
                float2 tMax   = (nb - g) / rdSafe;
                float  thickness = 0.0;
                float  prevT     = 0.0;
                // Burrow-shell softening (see _WallBurrowTex header). A burrow cell is open (not in
                // _OccluderDist) but its solid shell must occlude. While the ray is INSIDE a burrow (B)
                // accumulate its chord length in `pending`; when it crosses a burrow wall (R/G) that
                // chord was real shell between this fragment and an external light → commit it as
                // thickness. Reaching the light still inside (interior torch) or leaving via the open
                // door (no wall crossing) discards it → not shadowed. Grazing rays cut a shorter chord
                // → soft penumbra. BurrowThick scales the committed chord: it's the burrow-edge
                // softness knob (lower = wider/softer shadow edge, but a shallow burrow leaks more
                // light; higher = harder edge, thin burrows stay fully dark). Burrow-only — terrain
                // shadow softness is WallFade, left untouched.
                const float BurrowThick = 1.0;
                // BurrowBleed: the crossed wall goes transparent as the light nears it (committed block
                // scales with the light's distance from the wall, ramping over BurrowBleed tiles).
                // Without it a burrow snaps lit↔dark the instant the light crosses its wall line; with
                // it the burrow fades in as the light approaches and two adjacent burrows cross-fade.
                // Larger = longer/softer fade but more light leaks through walls near a light.
                const float BurrowBleed = 1.0;
                bool  inside  = SAMPLE_TEXTURE2D(_WallBurrowTex, sampler_WallBurrowTex, ((float2)cell + 0.5) / _WallTexSize.xy).b > 0.5;
                float pending = 0.0;
                [loop]
                for (int i = 0; i < 48; i++) {
                    float  nextT  = min(min(tMax.x, tMax.y), dist);  // exit param of current cell (clamped to light)
                    float  segLen = nextT - prevT;
                    float2 uv     = ((float2)cell + 0.5) / _GridSize.xy;
                    if (SAMPLE_TEXTURE2D(_OccluderDist, sampler_OccluderDist, uv).r < 0.5)
                        thickness += segLen;                         // solid terrain inside this cell
                    if (inside) pending += segLen;                   // chord inside the burrow shell
                    if (nextT >= dist) break;                        // reached the light
                    prevT = nextT;
                    float bleed = saturate((dist - prevT) / BurrowBleed);
                    // Soft burrow wall. Read the wall mask BILINEARLY at the true crossing point,
                    // blending only ALONG the wall (the perpendicular axis is pinned to the texel
                    // centre so walls don't smear sideways). A ray crossing mid-wall reads w≈1 (full
                    // block); one grazing a corner or door jamb reads a partial w → partial commit →
                    // the diagonal shadow boundary fades instead of snapping (same soft feel terrain
                    // corners get from continuous thickness). The w fraction of the chord commits as
                    // shell thickness; the (1−w) that "passed through" the soft edge keeps marching.
                    float w;
                    if (tMax.x < tMax.y) {
                        int   lineX  = (stp.x > 0) ? cell.x + 1 : cell.x;   // vertical line being crossed
                        float crossY = p.y + rd.y * prevT;                  // world Y where the ray meets it
                        float2 wuv   = float2((lineX + 0.5) / _WallTexSize.x, (crossY + 0.5) / _WallTexSize.y);
                        w = SAMPLE_TEXTURE2D(_WallBurrowTex, sampler_WallBurrowTex, wuv).r;
                        tMax.x += tDelta.x; cell.x += stp.x;
                    } else {
                        int   lineY  = (stp.y > 0) ? cell.y + 1 : cell.y;   // horizontal line being crossed
                        float crossX = p.x + rd.x * prevT;                  // world X where the ray meets it
                        float2 wuv   = float2((crossX + 0.5) / _WallTexSize.x, (lineY + 0.5) / _WallTexSize.y);
                        w = SAMPLE_TEXTURE2D(_WallBurrowTex, sampler_WallBurrowTex, wuv).g;
                        tMax.y += tDelta.y; cell.y += stp.y;
                    }
                    thickness += pending * BurrowThick * bleed * w;
                    pending   *= (1.0 - w);
                    // Re-derive "am I inside a burrow" from the interior mask at the cell we stepped
                    // into, rather than blind-toggling — correct across interior edges, the door, and
                    // two adjacent burrows sharing a wall (the toggle mishandled burrow→burrow).
                    inside = SAMPLE_TEXTURE2D(_WallBurrowTex, sampler_WallBurrowTex, ((float2)cell + 0.5) / _WallTexSize.xy).b > 0.5;
                }
                return thickness;   // any still-pending in-burrow chord is intentionally discarded
            }

            struct Attributes {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float2 worldPos   : TEXCOORD1;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float3 worldPos3 = TransformObjectToWorld(IN.positionOS);
                OUT.positionCS   = TransformWorldToHClip(worldPos3);
                OUT.uv           = IN.uv;
                OUT.worldPos     = worldPos3.xy;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                // Radial falloff.
                float2 d       = IN.uv - 0.5;
                float  r       = length(d);
                float  inner   = _InnerFraction * 0.5;
                float  falloff = 1.0 - smoothstep(inner, 0.5, r);
                falloff = falloff * falloff;

                // Flood-fill: replace the radial falloff with this light's geodesic reach (which
                // already bakes in around-corner falloff + occlusion). Valid only when the light has
                // a baked field (_ReachRect.z > 0); otherwise fall back to the radial value above.
                bool useReach = (_FloodFill > 0.5) && (_ReachRect.z > 0.0);
                if (useReach) {
                    float2 ruv = (IN.worldPos.xy - _ReachRect.xy) / _ReachRect.zw;
                    falloff = SAMPLE_TEXTURE2D(_ReachTex, sampler_ReachTex, ruv).r;
                }

                // Sample world-space normals RT at this fragment's screen position.
                // No Y-flip: DrawRenderers writes _CapturedNormalsRT in OpenGL convention
                // (V=0 at bottom). positionCS.y is also 0 at the bottom (renderIntoTexture:false
                // projection used by LightPass), so they match directly. Flipping here
                // displaces normals vertically and inverts the Y lighting direction.
                float2 screenUV = IN.positionCS.xy / _ScreenParams.xy;
                float4 ns = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, screenUV);

                // Directional-only tier (alpha ≈ 0.3): skip torch contribution entirely.
                // LightComposite still applies the lightmap to these pixels, but since we
                // write nothing here the lightmap only carries ambient + sun for them.
                if (ns.a > 0.0 && ns.a < 0.4) return float4(0, 0, 0, 1);

                // Alpha = 0 means no sprite: use flat camera-facing fallback.
                // (We test alpha, not rgb, because B now carries the sort bucket
                // and can be non-zero even with a flat forward normal.)
                float2 nxy;
                if (ns.a < 0.01) nxy = float2(0, 0);
                else             nxy = ns.rg * 2.0 - 1.0;

                // Reconstruct normal.z from xy. All sprite normal maps in this
                // project have z ≤ 0 (camera-facing), so we take the negative root.
                float nz = -sqrt(saturate(1.0 - dot(nxy, nxy)));
                float3 normal = float3(nxy, nz);

                // Sort-aware lighting:
                //   In front (sortDelta > 0): flip effective height so the light appears
                //     to come from behind the receiver. Forward-facing interior normals
                //     then have negative NdotL (clamped to 0), while edge normals pointing
                //     sideways toward the light's XY still catch it. Ambient floor = 0
                //     prevents the radial falloff from bleeding warm light through the
                //     silhouette.
                //   Behind (sortDelta <= 0): effective height ramps from _LightHeight
                //     (at delta=0) toward _LightHeight * _BehindFarHeightFactor as the
                //     receiver sorts further behind. Default _BehindFarHeightFactor = 1.0
                //     makes this the identity — matches pre-change lighting. Ambient
                //     floor stays as _AmbientNormal.
                float sortDelta = ns.b - _LightSortBucket;
                float effectiveHeight;
                if (sortDelta > 0.0) {
                    effectiveHeight = -_LightHeight;
                } else {
                    float behindT = smoothstep(0.0, max(_SortRampRange, 1e-5), -sortDelta);
                    effectiveHeight = lerp(_LightHeight, _LightHeight * _BehindFarHeightFactor, behindT);
                }
                // EXPERIMENT: ambient floor applied on both sides — radial falloff bleeds
                // through silhouettes again. Set to 0.0 in the in-front branch to restore
                // hard-block behaviour.
                float ambientFloor = _AmbientNormal;

                float3 toLight = normalize(float3(_LightWorldPos.xy - IN.worldPos.xy, -effectiveHeight));
                // Center-flatten (respects normals, removes the distance hot-spot): divide the
                // raw NdotL by the flat-surface response (-toLight.z — what a flat camera-facing
                // patch gets at this distance) so a flat surface reads uniformly across the disc
                // instead of peaking under the light. Normals still modulate: faces toward the
                // light brighten up to hiCeil, recesses/away-facing normals darken. The ceiling
                // scales with _CenterFlatten so flattening also re-opens the highlight range that
                // a flat clamp would otherwise remove — restoring normal "pop" without a separate
                // knob. At _CenterFlatten = 0 both collapse (divisor 1, ceiling 1) → raw NdotL.
                // The 0.2 floor on flatRef keeps the divide stable far out and in the in-front
                // silhouette-block branch (where -toLight.z < 0).
                float flatRef = max(0.2, -toLight.z);
                float hiCeil  = lerp(1.0, 1.3, _CenterFlatten);   // highlight headroom grows with flatten
                float ndotl   = min(hiCeil, dot(normal, toLight) / lerp(1.0, flatRef, _CenterFlatten));
                ndotl = max(ambientFloor, ndotl);

                // Wall shadows = thickness-attenuated soft occlusion. Instead of a binary
                // "ray crosses a wall → fully black" test (which snaps at tile boundaries and
                // looks like a hard cutout), accumulate how much SOLID material the ray to the
                // light passes through (SolidThickness, exact DDA) and ramp to full shadow over
                // WallFade tiles of it. A straight shot through a 1-tile wall accumulates ~1 tile
                // → fully blocked (no thin-wall leak); a ray that just clips a corner accumulates
                // a fraction → partial shadow. As the light slides across a wall edge that
                // grazing length grows continuously, so the receiver fades lit→dark over ~WallFade
                // tiles — the same gradual feel as the tile edge-depth darkening, not a hard line.
                // Only NON-solid receivers trace (ns.a < 0.78): a wall's own lit face is lit by the
                // radial falloff; a solid receiver sits at distance 0 and would self-shadow.
                float shadow = 0.0;
                float2 toL = _LightWorldPos.xy - IN.worldPos.xy;
                float  distL = length(toL);
                if (!useReach && _PointShadows > 0.5 && ns.a < 0.78 && distL > 1e-4) {
                    const float WallFade = 0.5;                    // tiles of solid to reach full shadow (lower = harder edge)
                    float thick = SolidThickness(IN.worldPos.xy, _LightWorldPos.xy);
                    shadow = saturate(thick / WallFade);
                }

                return float4(saturate(_LightColor.rgb * (_Intensity * falloff * ndotl * (1.0 - shadow))), 1.0);
            }
            ENDHLSL
        }
    }
}
