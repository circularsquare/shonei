// Renders the water sprite into the normals RT.
// Unlike NormalsCapture.shader, this uses the global _WaterSurfaceTex (set each
// tick by WaterController) for transparency — so only pixels with actual water
// are written, and transparent water pixels remain black (flat fallback).
// Outputs flat forward normals since the water surface is a flat plane.
//
// Pass 0 — shadow casters (alpha = 1.0)  — unused for water; included for completeness
// Pass 1 — lit-only, no shadow (alpha = 0.5) — used: water is lit by torches but casts no shadow
// Pass 2 — directional-only (alpha = 0.3)    — unused for water; included for completeness
Shader "Hidden/NormalsCaptureWater" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Set globally each tick by WaterController.UpdateSurfaceMask().
        // R channel: 0 = no water, 0.5 = interior water, 1.0 = surface water.
        TEXTURE2D(_WaterSurfaceTex);
        SAMPLER(sampler_WaterSurfaceTex);

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

        float4 FragWater(Varyings IN, float shadowAlpha) {
            float mask = SAMPLE_TEXTURE2D(_WaterSurfaceTex, sampler_WaterSurfaceTex, IN.uv).r;
            // Discard pixels with no water — they stay black in the normals RT (flat fallback).
            clip(mask - 0.25);
            // Flat camera-facing normal: world (0, 0, -1) packed → (0.5, 0.5, 0.0).
            return float4(0.5, 0.5, 0.0, shadowAlpha);
        }
        ENDHLSL

        // Pass 0: shadow casters — alpha = 1.0
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN) : SV_Target { return FragWater(IN, 1.0); }
            ENDHLSL
        }

        // Pass 1: lit-only, no shadow — alpha = 0.5
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN) : SV_Target { return FragWater(IN, 0.5); }
            ENDHLSL
        }

        // Pass 2: directional-only, no shadow — alpha = 0.3
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN) : SV_Target { return FragWater(IN, 0.3); }
            ENDHLSL
        }
    }
}
