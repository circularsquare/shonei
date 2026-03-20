// Full-screen multiply blit: multiplies the scene by the custom light map.
// final = scene * lightmap  (black RT = black scene, white RT = unchanged scene)
// Used with cmd.Blit, which sets _MainTex automatically.
//
// Only applies lighting to pixels where a sprite was rendered (normals RT alpha > 0).
// Empty sky/background pixels (alpha == 0) return (1,1,1,1) — a no-op multiply — so
// they are left unmodified. Sky color is instead tinted by BackgroundCamera.backgroundColor.
Shader "Hidden/LightComposite" {
    Properties {
        _MainTex ("Light Map", 2D) = "white" {}
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

                // No sprite here (empty sky/background) — no-op multiply.
                if (normsAlpha < 0.25) return float4(1, 1, 1, 1);

                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
            }
            ENDHLSL
        }
    }
}
