// Renders all sprites into a world-space normals RT owned by LightFeature.
// Each sprite's _NormalMap (tangent-space, RGBA32) is decoded and transformed
// to world-space for a flat 2D sprite: tangent (x,y,z) → world (x, y, -z).
// Output is packed 0–1: float3(worldNormal * 0.5 + 0.5).
// Alpha encodes lighting tier:
//   0.80–1.0 = shadow caster (tiles/buildings) — range encodes edge depth for light penetration
//   0.5 = lit-only, no shadow (decorations)  — full light, no shadow cast
//   0.3 = directional-only (clouds, etc.)    — sun + ambient only, no torch/point lights
//   0.0 = no sprite (cleared black — flat normal fallback in LightSun)
// Transparent pixels are discarded so the background stays black (flat fallback).
//
// Tiles set _AdjacencyMask (0–15) via MaterialPropertyBlock. Non-tile sprites
// get the material default of 15 (no exposed edges = no jagged clipping).
//
// Pass 0 — shadow casters (alpha = edge depth mapped to 0.80–1.0)
// Pass 1 — lit-only, no shadow (alpha = 0.5)
// Pass 2 — directional-only, no shadow (alpha = 0.3)
Shader "Hidden/NormalsCapture" {
    Properties {
        _AdjacencyMask ("Adjacency Mask", Float) = 15
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Assets/Lighting/TileEdge.hlsl"

        TEXTURE2D(_MainTex);   SAMPLER(sampler_MainTex);
        TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);

        float _AdjacencyMask; // 0–15 via MPB, default 15 on override material

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
            float3 wp      = TransformObjectToWorld(IN.positionOS);
            OUT.positionCS = TransformWorldToHClip(wp);
            OUT.uv         = IN.uv;
            OUT.worldPos   = wp.xy;
            return OUT;
        }

        float4 FragWithAlpha(Varyings IN, bool isFrontFace, float shadowAlpha) {
            // Discard transparent pixels — background stays black (flat fallback).
            float alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
            clip(alpha - 0.1);

            // Jagged edge clip — only affects tiles with _AdjacencyMask < 15.
            // Non-tile sprites have _AdjacencyMask = 15 (set on override material),
            // so TileEdgeClip returns true immediately.
            if (!TileEdgeClip(_AdjacencyMask, IN.worldPos))
                discard;

            // Tangent-space normal, RGBA32 packed 0–1.
            float4 ns = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, IN.uv);
            float3 tn = ns.rgb * 2.0 - 1.0;

            // SpriteRenderer.flipX negates mesh vertex X, reversing winding → back face.
            // Negate tangent X to keep the world-space normal pointing the right way.
            if (!isFrontFace) tn.x = -tn.x;

            // For flat 2D sprites facing the camera:
            // tangent X → world +X, tangent Y → world +Y, tangent Z → world -Z.
            float3 wn = normalize(float3(tn.x, tn.y, -tn.z));

            // For shadow casters (pass 0), encode edge depth in alpha.
            // _NormalMap.a carries edge-distance falloff from TileNormalMaps:
            //   1.0 = at exposed edge (fully lit), 0.0 = deep interior (dark).
            // Non-tile sprites have _NormalMap.a = 1.0 → lerp(0.80, 1.0, 1.0) = 1.0.
            float outAlpha = shadowAlpha;
            if (shadowAlpha > 0.75)
                outAlpha = lerp(0.80, 1.0, ns.a);

            return float4(wn * 0.5 + 0.5, outAlpha);
        }
        ENDHLSL

        // Pass 0: shadow casters — alpha = edge depth mapped to 0.80–1.0
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                return FragWithAlpha(IN, isFrontFace, 1.0);
            }
            ENDHLSL
        }

        // Pass 1: lit-only, no shadow — alpha = 0.5
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                return FragWithAlpha(IN, isFrontFace, 0.5);
            }
            ENDHLSL
        }

        // Pass 2: directional-only, no shadow — alpha = 0.3
        // Sprites on this pass receive sun + ambient light only.
        // LightCircle.shader skips pixels with alpha < 0.4, so torch/point lights
        // never reach these sprites even though LightComposite applies the lightmap.
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                return FragWithAlpha(IN, isFrontFace, 0.3);
            }
            ENDHLSL
        }
    }
}
