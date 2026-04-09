// Full-screen multiply blit: multiplies the scene by the custom light map.
// final = scene * lightmap  (black RT = black scene, white RT = unchanged scene)
// Used with cmd.Blit, which sets _MainTex automatically.
//
// Sprite pixels (normals RT alpha > 0) get the full lightmap applied.
// Empty sky/background pixels (alpha == 0) use a precomputed _SkyLightColor
// (sun + time-of-day ambient, no sky-exposure modulation, no point lights),
// blended via _SkyLightBlend. Remaining sky color = SkyCamera.backgroundColor.
Shader "Hidden/LightComposite" {
    Properties {
        _MainTex ("Light Map", 2D) = "white" {}
        _SkyLightBlend ("Sky Light Blend", Range(0, 1)) = 0.1
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        Blend DstColor Zero
        BlendOp Add
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "LightComposite"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _SkyLightBlend;
            float4 _SkyLightColor; // precomputed sun + ambient for sky pixels (no exposure)
            float4 _DeepAmbient;

            // Normals RT alpha > 0 means main camera rendered a sprite at this pixel.
            TEXTURE2D(_CapturedNormalsRT);
            SAMPLER(sampler_CapturedNormalsRT);

            struct Attributes {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float normsAlpha = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, IN.uv).a;

                // No sprite here (empty sky/background) — use precomputed
                // sun + ambient color (no sky-exposure, no point lights).
                if (normsAlpha < 0.25) {
                    return lerp(float4(1, 1, 1, 1), _SkyLightColor, _SkyLightBlend);
                }

                float4 light = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);

                // Edge-depth blending: shadow-caster pixels deep inside tiles
                // blend toward deepAmbient based on distance from exposed surface.
                // edgeDepth: 1.0 at surface (full light), 0.0 at penetration depth (deepAmbient).
                // Lerp guarantees deep interiors are exactly deepAmbient everywhere,
                // regardless of sky/sun contribution in the light RT.
                if (normsAlpha > 0.75) {
                    float edgeDepth = saturate((normsAlpha - 0.80) / 0.20);
                    light.rgb = lerp(_DeepAmbient.rgb, light.rgb, edgeDepth);
                }

                return light;
            }
            ENDHLSL
        }
    }
}
