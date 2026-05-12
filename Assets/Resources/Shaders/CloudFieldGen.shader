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
//   _Blobs             Vector4[256] array of sphere-blobs
//                      xy = sprite-local centre (world units)
//                      z  = depth offset (world units)
//                      w  = radius (world units)
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
            float  _SilhouetteWarpStrength;
            float  _SilhouetteWarpScale;
            float  _EdgeThreshold;
            float  _EdgeSoftness;
            float4 _Blobs[MAX_BLOBS];
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

        // Domain-warp the pixel's sample position via two ValueNoise
        // taps. The warp is sampled at the noise-anchored coordinate
        // (lp + _NoiseOffset) so it scrolls with the cloud body as
        // wind drifts the noise field — without this term, the wobble
        // would stay glued to the viewport and the cloud would appear
        // to slide through a stationary warp pattern. Affects both
        // silhouette (blob distance check uses the warped position)
        // and shading (sphere normals point from the un-warped blob
        // centre toward the warped pixel, breaking up the obviously-
        // circular Lambertian).
        float2 WarpLocal(float2 lp) {
            float2 q = (lp + _NoiseOffset) * _SilhouetteWarpScale;
            // Two noise taps with offset seeds give an independent
            // [0,1] for each axis; remap to [-1,+1] and scale by the
            // user-tunable strength.
            float2 w = float2(
                ValueNoise(q),
                ValueNoise(q + float2(91.4, 47.2))
            ) * 2.0 - 1.0;
            return lp + w * _SilhouetteWarpStrength;
        }
        ENDHLSL

        // Pass 0 — stitched height-field shading into mainRT.
        Pass {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment fragMask

            half4 fragMask(Varyings IN) : SV_Target {
                float2 lp = WarpLocal(SpriteLocalFromUV(IN.uv));

                // Single-pass shading. For every blob the pixel sits
                // inside, accumulate (sphere surface normal × metaball
                // influence). The normalized weighted sum gives a
                // continuous normal across the cloud: pixels deep inside
                // one blob get that sphere's normal (closest to the
                // sphere has the strongest weight), pixels in a crack
                // between two overlapping blobs get a smooth blend of
                // both spheres' normals. No more max() discontinuity at
                // sphere junctions — the cracks fill in naturally with
                // contributions from every covering sphere.
                //
                // The same metaball influences also feed totalInf for
                // the smoothstep-thresholded coverage / alpha.
                float3 nAccum   = float3(0, 0, 0);
                float  totalInf = 0;

                [loop] for (int i = 0; i < _BlobCount; i++) {
                    float4 b     = _Blobs[i];
                    float  r2    = b.w * b.w;
                    float2 d     = lp - b.xy;
                    float  dist2 = dot(d, d);
                    if (dist2 < r2) {
                        float dz       = sqrt(r2 - dist2);
                        float w        = saturate(1.0 - dist2 / r2);
                        // Sphere surface normal at this xy on a sphere
                        // centred at b.xy with radius b.w. Length = 1
                        // since (d.x, d.y, dz) lies on the sphere of
                        // radius b.w.
                        float3 sphereN = float3(d, dz) / b.w;
                        nAccum   += sphereN * w;
                        totalInf += w;
                    }
                }

                float coverage = smoothstep(_EdgeThreshold - _EdgeSoftness, _EdgeThreshold + _EdgeSoftness, totalInf);
                if (coverage < 1e-3) return half4(0, 0, 0, 0);

                // Default to camera-facing normal if all weights round to
                // zero (pixel sitting right at every blob's outer edge
                // simultaneously — vanishingly rare, but safe).
                float3 normal = (totalInf > 1e-6)
                              ? normalize(nAccum)
                              : float3(0, 0, 1);

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
                float2 lp = WarpLocal(SpriteLocalFromUV(IN.uv));
                float totalInf = 0;
                [loop] for (int i = 0; i < _BlobCount; i++) {
                    float4 b  = _Blobs[i];
                    float2 d  = lp - b.xy;
                    float  d2 = dot(d, d);
                    float  r2 = b.w * b.w;
                    totalInf += saturate(1.0 - d2 / r2);
                }
                float coverage = smoothstep(_EdgeThreshold - _EdgeSoftness, _EdgeThreshold + _EdgeSoftness, totalInf);
                return half4(0.5, 0.5, 1.0, coverage);
            }
            ENDHLSL
        }
    }
}
