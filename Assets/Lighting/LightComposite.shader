// Full-screen multiply blit: multiplies the scene by the custom light map.
// final = scene * lightmap  (black RT = black scene, white RT = unchanged scene)
// Used with cmd.Blit, which sets _MainTex automatically.
//
// Only applies lighting where the main camera rendered a sprite (normals RT alpha > 0).
// Pixels with no main-camera sprite (clouds from background camera, empty space) return
// (1,1,1,1) — a no-op multiply — so background camera lighting is preserved untouched.
// This prevents torch light bleeding onto clouds and avoids double-multiplying background pixels.
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

                // No main-camera sprite here — multiply by 1 (no-op) to leave background
                // camera output (clouds, sky) with their own lighting intact.
                if (normsAlpha < 0.25) return float4(1, 1, 1, 1);

                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
            }
            ENDHLSL
        }
    }
}
