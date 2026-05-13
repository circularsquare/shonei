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
            float  _EdgeWobbleStrength;
            float  _EdgeWobbleScale;
            float  _EdgeThreshold;
            float  _EdgeSoftness;
            float  _BlobAspect;
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

        // Per-pixel edge-wobble: instead of warping the blob-distance
        // sampling position (which previously twisted both silhouette
        // and interior shading), we perturb only the metaball alpha
        // threshold. The blob loop runs on un-warped positions — so
        // sphere normals stay clean and untwisted — but each pixel's
        // smoothstep cut point is shifted by ±_EdgeWobbleStrength
        // around _EdgeThreshold according to a single noise tap.
        // Result: silhouettes ripple organically without the cloud
        // interior looking like a melted swirl.
        //
        // Sampled at the noise-anchored coordinate (lp + _NoiseOffset)
        // so the wobble pattern travels with the cloud body as wind
        // drifts the noise field, rather than staying glued to the
        // viewport.
        float EdgeThresholdAt(float2 lp) {
            float n = ValueNoise((lp + _NoiseOffset) * _EdgeWobbleScale) * 2.0 - 1.0;
            return _EdgeThreshold + n * _EdgeWobbleStrength;
        }
        ENDHLSL

        // Pass 0 — stitched height-field shading into mainRT.
        Pass {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment fragMask

            half4 fragMask(Varyings IN) : SV_Target {
                float2 lp = SpriteLocalFromUV(IN.uv);

                // Single-pass shading. For every blob the pixel sits
                // inside, accumulate (ellipsoid surface normal × metaball
                // influence). The normalized weighted sum gives a
                // continuous normal across the cloud: pixels deep inside
                // one blob get that ellipsoid's normal; pixels in a
                // crack between two overlapping blobs get a smooth
                // blend of both. No max() discontinuity at junctions —
                // the cracks fill in naturally.
                //
                // Each blob is an ellipsoid stretched horizontally by
                // _BlobAspect (1 = sphere, >1 = horizontally elongated
                // lobe). We work in the blob's "scaled" coordinate frame
                // where it looks like a unit sphere: scale d.x by
                // 1 / _BlobAspect for the distance/influence check and
                // for the surface "depth" z, then unscale x in the
                // normal so it points in true world directions.
                //
                // The same metaball influences also feed totalInf for
                // the alpha-threshold smoothstep.
                float invAspect = 1.0 / _BlobAspect;
                float3 nAccum   = float3(0, 0, 0);
                float  totalInf = 0;

                [loop] for (int i = 0; i < _BlobCount; i++) {
                    float4 b     = _Blobs[i];
                    float  r2    = b.w * b.w;
                    float2 d     = lp - b.xy;
                    // Ellipsoid → unit-sphere by squashing x.
                    float2 dS    = float2(d.x * invAspect, d.y);
                    float  dist2 = dot(dS, dS);
                    if (dist2 < r2) {
                        float dz = sqrt(r2 - dist2);
                        float w  = saturate(1.0 - dist2 / r2);
                        // Ellipsoid surface normal: gradient of
                        // F = (x/aspect)² + y² + z² − r² gives
                        // (2x/aspect², 2y, 2z). Normalize after.
                        float3 sphereN = normalize(float3(d.x * invAspect * invAspect, d.y, dz));
                        nAccum   += sphereN * w;
                        totalInf += w;
                    }
                }

                float threshold = EdgeThresholdAt(lp);
                float coverage = smoothstep(threshold - _EdgeSoftness, threshold + _EdgeSoftness, totalInf);
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
                float2 lp = SpriteLocalFromUV(IN.uv);
                float invAspect = 1.0 / _BlobAspect;
                float totalInf = 0;
                [loop] for (int i = 0; i < _BlobCount; i++) {
                    float4 b  = _Blobs[i];
                    float2 d  = lp - b.xy;
                    float2 dS = float2(d.x * invAspect, d.y);
                    float  d2 = dot(dS, dS);
                    float  r2 = b.w * b.w;
                    totalInf += saturate(1.0 - d2 / r2);
                }
                float threshold = EdgeThresholdAt(lp);
                float coverage = smoothstep(threshold - _EdgeSoftness, threshold + _EdgeSoftness, totalInf);
                return half4(0.5, 0.5, 1.0, coverage);
            }
            ENDHLSL
        }
    }
}
