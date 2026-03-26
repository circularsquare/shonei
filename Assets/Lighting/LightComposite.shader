// Full-screen multiply blit: multiplies the scene by the custom light map.
// final = scene * lightmap  (black RT = black scene, white RT = unchanged scene)
// Used with cmd.Blit, which sets _MainTex automatically.
//
// Sprite pixels (normals RT alpha > 0) get the full lightmap applied.
// Empty sky/background pixels (alpha == 0) blend _SkyLightBlend of the lightmap in,
// so the sun subtly tints the sky (warm sunset glow, etc.). The remaining sky color
// comes from BackgroundCamera.backgroundColor.
Shader "Hidden/LightComposite" {
    Properties {
        _MainTex ("Light Map", 2D) = "white" {}
        _SkyLightBlend ("Sky Light Blend", Range(0, 1)) = 0.4
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

                // No sprite here (empty sky/background) — blend in a fraction of the
                // lightmap so the sun subtly tints the sky (warm sunset, etc.).
                if (normsAlpha < 0.25) {
                    float4 light = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                    return lerp(float4(1, 1, 1, 1), light, _SkyLightBlend);
                }

                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
            }
            ENDHLSL
        }
    }
}
