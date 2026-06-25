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

            // Populated each frame by NormalsCapturePass.
            TEXTURE2D(_CapturedNormalsRT);
            SAMPLER(sampler_CapturedNormalsRT);

            // Occluder distance field (OccluderField.cs): per-tile world-space distance (tiles) to
            // the nearest wall, bilinear. 0 inside walls, >=1 in open cells. _GridSize = (nx, ny).
            // Walked by SolidThickness for the soft thickness-attenuated shadow.
            TEXTURE2D(_OccluderDist);
            SAMPLER(sampler_OccluderDist);
            float4 _GridSize;

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
                [loop]
                for (int i = 0; i < 48; i++) {
                    float  nextT = min(min(tMax.x, tMax.y), dist);   // exit param of current cell (clamped to light)
                    float2 uv    = ((float2)cell + 0.5) / _GridSize.xy;
                    if (SAMPLE_TEXTURE2D(_OccluderDist, sampler_OccluderDist, uv).r < 0.5)
                        thickness += nextT - prevT;                  // length of the ray inside this solid cell
                    if (nextT >= dist) break;                        // reached the light
                    prevT = nextT;
                    if (tMax.x < tMax.y) { tMax.x += tDelta.x; cell.x += stp.x; }
                    else                 { tMax.y += tDelta.y; cell.y += stp.y; }
                }
                return thickness;
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
                if (_PointShadows > 0.5 && ns.a < 0.78 && distL > 1e-4) {
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
