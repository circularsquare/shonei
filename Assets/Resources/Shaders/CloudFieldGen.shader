// Procedural cloud-field generation. The cloud body is a CPU-spawned
// constellation of 3D sphere-blobs (placed at noise-density local
// maxima by CloudLayer.cs each frame) rendered as a 2D sprite via two
// passes written into RenderTextures by Graphics.Blit:
//
//   Pass 0 — mainRT (RGBA): treats the blob array as a HEIGHT FIELD
//            `h(x, y) = max_i(zTop_i)` where `zTop_i = blob.z +
//            sqrt(r² - dist²)` for any blob the pixel sits inside. The
//            surface normal at each pixel comes from a finite-
//            difference gradient of this height field (5-tap: centre +
//            4 cardinal neighbours), so adjacent blobs whose surfaces
//            meet at a pixel automatically blend their normals — the
//            cloud reads as one continuous body with bumps, instead of
//            looking like discrete circles glued together. Lambertian
//            against the global _SunDir then quantizes into 3 discrete
//            colour bands.
//
//            Alpha is a metaball-style merged silhouette: each blob
//            contributes a linear influence `saturate(1 − dist²/r²)`,
//            summed across all blobs, smoothstep-thresholded. Adjacent
//            blobs merge into a single body (their influences add over
//            the overlap region); the silhouette is smooth, not a
//            bumpy union of circles.
//
//   Pass 1 — normalRT (RGBA): FLAT tangent normal (0,0,1) for every
//            pixel; alpha matches Pass 0's metaball coverage so the
//            global NormalsCapture clip lines up with the visible
//            silhouette exactly.
//
// Per-material uniforms (set per-frame from CloudLayer.LateUpdate):
//   _TexSize           (w, h) of the destination RT in pixels
//   _InvPpu            1 / pixelsPerUnit, for UV → sprite-local world units
//   _LitColor          sunlit band colour
//   _MidColor          mid-tone band colour
//   _ShadowColor       shadow band colour
//   _LitBand           ndotl > _LitBand    → _LitColor
//   _ShadowBand        ndotl < _ShadowBand → _ShadowColor (else _MidColor)
//   _NormalEpsilon     finite-difference step (world units) for the
//                      height-field gradient; smaller = sharper per-blob
//                      facets, larger = smoother blob blending
//   _Blobs             Vector4[MAX_BLOBS] array of sphere-blobs
//                      xy = sprite-local centre (world units)
//                      z  = depth offset (world units)
//                      w  = radius (world units)
//   _BlobAspects       float[MAX_BLOBS] — per-blob horizontal stretch
//                      (parallel to _Blobs). Each blob renders as an
//                      ellipsoid with x-axis scaled by its own aspect,
//                      so neighbours don't all look identical.
//   _BlobCount         active count in _Blobs; entries past this are stale
//
// Globals consumed (broadcast by LightFeature.cs):
//   _SunDir.xy    world direction toward the sun (z=0)
//   _SunHeight    sun elevation
Shader "Hidden/CloudFieldGen" {
    SubShader {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "../../Lighting/Noise.hlsl"

        // Must match MAX_BLOBS in CloudLayer.cs. 512 × float4 = 8 KB.
        #define MAX_BLOBS 512

        // Metaball alpha smoothstep bounds derived from inspector-tunable
        // _EdgeThreshold (centre) and _EdgeSoftness (half-width). Single-
        // blob metaball influence at its centre is 1, at its edge is 0 —
        // so totalInf > centre + softness reads fully solid, totalInf
        // below centre - softness is transparent. Widening softness
        // gives wispier silhouettes; raising the centre shrinks them.

        CBUFFER_START(UnityPerMaterial)
            float2 _TexSize;
            float  _InvPpu;
            float4 _LitColor;
            float4 _MidColor;
            float4 _ShadowColor;
            float  _LitBand;
            float  _ShadowBand;
            float  _CloudSunHeight;
            float2 _NoiseOffset;
            float  _EdgeWobbleStrength;
            float  _EdgeWobbleScale;
            float  _CurlEps;
            float  _NormalEpsilon;
            float  _EdgeThreshold;
            float  _EdgeSoftness;
            float4 _Blobs[MAX_BLOBS];
            // Per-blob horizontal stretch (parallel array to _Blobs).
            // Declared as float; HLSL cbuffer rules pack each scalar
            // into its own float4 slot (only .x meaningful) — matches
            // what Unity expects for Material.SetFloatArray.
            float  _BlobAspects[MAX_BLOBS];
            int    _BlobCount;
        CBUFFER_END

        // ── Globals — declared OUTSIDE the CBUFFER ──────────────────────
        // _SunDir / _SunHeight are SetGlobal'd by LightFeature each frame.
        // Putting globals inside UnityPerMaterial breaks SRP batcher.
        float3 _SunDir;
        float  _SunHeight;

        struct Attributes {
            float3 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
        };

        struct Varyings {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
        };

        Varyings vert(Attributes IN) {
            Varyings OUT;
            OUT.positionCS = TransformObjectToHClip(IN.positionOS);
            OUT.uv         = IN.uv;
            return OUT;
        }

        // UV [0,1] → sprite-local world position (centre = 0,0).
        float2 SpriteLocalFromUV(float2 uv) {
            return (uv - 0.5) * _TexSize * _InvPpu;
        }

        // 2-octave FBM of value noise. One octave looks too smooth at
        // realistic warp strengths; three is overkill for the curl-of-
        // FBM consumer (the derivative blows out the highest octave).
        // Two strikes a balance: silhouette wobble has a primary bump
        // shape plus a wisp of fine detail.
        float ValueNoiseFBM(float2 p) {
            float v = ValueNoise(p) + ValueNoise(p * 2.07) * 0.5;
            return v / 1.5;
        }

        // Curl-of-noise 2D warp displacement. The warp vector field is
        // the curl of a scalar potential ψ(p), which is divergence-
        // free — silhouettes shear and swirl locally rather than
        // expanding / contracting uniformly. Compared to two
        // independent noise samples per axis, curl looks more like
        // wind-shaped clouds (organic swirling lines at the edge) and
        // less like random pixel-level jitter.
        //
        // Returns a unit-ish vector in (∂ψ/∂y, −∂ψ/∂x) — caller
        // multiplies by the desired warp strength (world units).
        float2 CurlWarp(float2 p) {
            float eps = _CurlEps;
            float n_yp = ValueNoiseFBM(p + float2(0.0, eps));
            float n_yn = ValueNoiseFBM(p - float2(0.0, eps));
            float n_xp = ValueNoiseFBM(p + float2(eps, 0.0));
            float n_xn = ValueNoiseFBM(p - float2(eps, 0.0));
            float dpsi_dy = (n_yp - n_yn) * (0.5 / eps);
            float dpsi_dx = (n_xp - n_xn) * (0.5 / eps);
            return float2(dpsi_dy, -dpsi_dx);
        }

        // Accumulate max zTop at position p into bestZ. Used by the
        // 5-tap height-field finite-difference normal in fragMask.
        void AccumulateZ(float2 p, float4 b, float r2, float invAspect, inout float bestZ) {
            float2 d  = p - b.xy;
            float2 dS = float2(d.x * invAspect, d.y);
            float  d2 = dot(dS, dS);
            if (d2 < r2) {
                bestZ = max(bestZ, b.z + sqrt(r2 - d2));
            }
        }

        // Per-pixel silhouette wobble: a 2D **domain warp** applied to
        // the entire blob sample. Each pixel reads blob distances at
        // (lp + EdgeWarp(lp)) instead of at lp, so the cloud's whole
        // 3D form — silhouette AND interior shading — deforms by up
        // to _EdgeWobbleStrength world units. The shading bands wrap
        // around the wobbled bumps because the normals are computed
        // at the same warped position, not at the un-warped ellipsoid
        // underneath. Curl-noise warp is smooth, so adjacent pixels
        // sample nearby warped positions and normals vary smoothly —
        // no jagged shading.
        //
        // Sampled at (lp + _NoiseOffset) * scale so the wobble pattern
        // travels with the cloud body as wind drifts the noise field,
        // not glued to the viewport.
        //
        // Reduction inside the cloud body is automatic: totalInf
        // saturates deep in the body, so the smoothstep clips to 1
        // regardless of how the warp shifts contributions between
        // blobs. The wobble is only visible at the silhouette where
        // totalInf is in the transition band.
        float2 EdgeWarp(float2 lp) {
            float2 wp = (lp + _NoiseOffset) * _EdgeWobbleScale;
            return CurlWarp(wp) * _EdgeWobbleStrength;
        }
        ENDHLSL

        // Pass 0 — stitched height-field shading into mainRT.
        Pass {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment fragMask

            half4 fragMask(Varyings IN) : SV_Target {
                float2 lp = SpriteLocalFromUV(IN.uv);

                // For shading: pick the frontmost blob's normal at this
                // pixel — the blob with the highest zTop = b.z +
                // sqrt(r² − dist²). That blob's lobe is geometrically
                // in front of any others the pixel sits inside, so its
                // ellipsoid normal is the one the camera should see.
                // Adjacent overlapping blobs then read as one-in-front-
                // of-the-other (clean occlusion) rather than a fuzzy
                // weighted-average of normals across the overlap.
                //
                // For alpha: totalInf accumulates metaball influence
                // across ALL covering blobs, so silhouettes still merge
                // into a single soft body (the user-visible cloud
                // outline doesn't see the front/back split).
                //
                // Each blob is an ellipsoid stretched horizontally by
                // its own _BlobAspects[i] (1 = sphere, >1 = horizontally
                // elongated lobe). We work in the blob's "scaled"
                // coordinate frame where it looks like a unit sphere:
                // scale d.x by 1 / aspect for the distance check and
                // for the surface "depth" z, then unscale x in the
                // normal so it points in true world directions.
                // Per-blob (rather than global) so neighbours don't all
                // look like the same oval.

                // Domain-warped sample position. Both alpha and normal
                // sample from lp_alpha — the cloud's whole 3D form
                // (silhouette AND interior shading) deforms with the
                // warp, so the colour bands wrap around the wobbled
                // bumps instead of revealing the un-warped ellipsoids
                // underneath.
                float2 lp_alpha = lp + EdgeWarp(lp);

                // Treat the blob array as a HEIGHT FIELD
                //   h(p) = max_i(zTop_i(p)) where zTop_i = b.z +
                //   sqrt(r² − dist_i²(p)).
                // The cloud surface normal at each pixel comes from a
                // 5-tap finite-difference gradient of h (centre + 4
                // cardinal neighbours, step NORMAL_EPS).
                //
                // Why FD instead of pick-frontmost-blob's analytic
                // normal: where two blobs meet, max(·,·) has a ridge —
                // the chosen blob switches across it, and using only
                // that blob's ellipsoid normal makes the seam read as a
                // sharp kink in the shading. FD samples both sides of
                // the ridge and averages their gradients, producing a
                // smooth blend at the seam (which is geometrically
                // correct: at the ridge the surface IS flatter, both
                // blobs' surfaces tangent-meeting). Inside a single
                // blob's territory (same blob dominates at all 5 taps),
                // FD reduces to the analytic ellipsoid normal. At the
                // outer silhouette neighbour taps fall outside all
                // blobs (h = −∞) — extrapolated as a cliff so the
                // gradient points radially outward, recovering a
                // sphere-edge silhouette normal.
                float NORMAL_EPS = _NormalEpsilon;

                float h_c = -1e9, h_r = -1e9, h_l = -1e9, h_u = -1e9, h_d = -1e9;
                float totalInf = 0;
                float2 lp_r = lp_alpha + float2(NORMAL_EPS, 0);
                float2 lp_l = lp_alpha - float2(NORMAL_EPS, 0);
                float2 lp_u = lp_alpha + float2(0, NORMAL_EPS);
                float2 lp_d = lp_alpha - float2(0, NORMAL_EPS);

                [loop] for (int i = 0; i < _BlobCount; i++) {
                    float4 b  = _Blobs[i];
                    float  r2 = b.w * b.w;
                    float  invAspect = 1.0 / _BlobAspects[i];

                    // Centre tap: also accumulates alpha.
                    float2 d  = lp_alpha - b.xy;
                    float2 dS = float2(d.x * invAspect, d.y);
                    float  d2 = dot(dS, dS);
                    if (d2 < r2) {
                        h_c = max(h_c, b.z + sqrt(r2 - d2));
                        totalInf += saturate(1.0 - d2 / r2);
                    }
                    // 4 neighbour taps: height only.
                    AccumulateZ(lp_r, b, r2, invAspect, h_r);
                    AccumulateZ(lp_l, b, r2, invAspect, h_l);
                    AccumulateZ(lp_u, b, r2, invAspect, h_u);
                    AccumulateZ(lp_d, b, r2, invAspect, h_d);
                }

                // Clamp the smoothstep's lower bound at 0 so that pixels
                // outside every blob (totalInf == 0) always read fully
                // transparent. Without the clamp, threshold < softness
                // sends the lower bound negative and smoothstep(<0, >0, 0)
                // returns a nonzero value — painting a faint haze across
                // empty regions of the cloud quad.
                float lo = max(0.0, _EdgeThreshold - _EdgeSoftness);
                float coverage = smoothstep(lo, _EdgeThreshold + _EdgeSoftness, totalInf);
                if (coverage < 1e-3) return half4(0, 0, 0, 0);

                // Silhouette fallback: a neighbour tap that fell outside
                // all blobs (h still −∞) is treated as the surface
                // cliffing downward at slope 1, so the gradient points
                // outward at the cloud's outer boundary — sphere-edge
                // silhouette normal rather than a flat camera-facing
                // patch.
                h_r = (h_r < -1e8) ? h_c - NORMAL_EPS : h_r;
                h_l = (h_l < -1e8) ? h_c - NORMAL_EPS : h_l;
                h_u = (h_u < -1e8) ? h_c - NORMAL_EPS : h_u;
                h_d = (h_d < -1e8) ? h_c - NORMAL_EPS : h_d;

                float dhdx = (h_r - h_l) * (0.5 / NORMAL_EPS);
                float dhdy = (h_u - h_d) * (0.5 / NORMAL_EPS);
                // Surface z = h(x, y) → outward unit normal =
                // normalize(−∂h/∂x, −∂h/∂y, 1).
                float3 normal = normalize(float3(-dhdx, -dhdy, 1.0));

                // Lambertian in tangent space. We use _CloudSunHeight
                // (cloud-specific) instead of the scene's _SunHeight so
                // the cloud's terminator stays visibly curved (moon-
                // phase look) even when the scene's actual sun is at a
                // low elevation. _SunDir.xy still tracks the scene's
                // sun direction so the lit side rotates through the day
                // cycle. Cloud sprite is unrotated on the XY plane:
                // tangent +z = toward camera; world sun convention is
                // (_SunDir.xy, −_SunHeight), so tangent sun = (xy, +h).
                float3 sunDirT = normalize(float3(_SunDir.xy, _CloudSunHeight));
                float  ndotl   = saturate(dot(normal, sunDirT));

                half3 col = (ndotl > _LitBand)    ? _LitColor.rgb
                          : (ndotl > _ShadowBand) ? _MidColor.rgb
                                                  : _ShadowColor.rgb;
                return half4(col, coverage);
            }
            ENDHLSL
        }

        // Pass 1 — flat tangent normal + metaball alpha into normalRT.
        //
        // Tangent (0, 0, 1) → packed (0.5, 0.5, 1.0). NormalsCapture
        // decodes it via (rgb*2 − 1) to a camera-facing world normal,
        // so LightSun contributes a uniform brightness across the
        // cloud (no per-pixel N·L variation to smear the 3 colour
        // bands).
        //
        // Alpha reuses the metaball coverage so NormalsCapture's
        // _MainTex.a clip lines up exactly with Pass 0's visible
        // silhouette — no "drawn but unlit" pixels at the edges.
        Pass {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment fragNormal

            half4 fragNormal(Varyings IN) : SV_Target {
                float2 lp = SpriteLocalFromUV(IN.uv);
                // Domain-warp the alpha sample so NormalsCapture's clip
                // lines up with the visibly wobbled silhouette in Pass 0.
                float2 lp_alpha = lp + EdgeWarp(lp);
                float totalInf = 0;
                [loop] for (int i = 0; i < _BlobCount; i++) {
                    float4 b  = _Blobs[i];
                    float invAspect = 1.0 / _BlobAspects[i];
                    float2 d  = lp_alpha - b.xy;
                    float2 dS = float2(d.x * invAspect, d.y);
                    float  d2 = dot(dS, dS);
                    float  r2 = b.w * b.w;
                    totalInf += saturate(1.0 - d2 / r2);
                }
                // See alpha pass for why the lower bound is clamped at 0.
                float lo = max(0.0, _EdgeThreshold - _EdgeSoftness);
                float coverage = smoothstep(lo, _EdgeThreshold + _EdgeSoftness, totalInf);
                return half4(0.5, 0.5, 1.0, coverage);
            }
            ENDHLSL
        }
    }
}
