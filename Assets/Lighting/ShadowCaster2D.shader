// Renders sprite silhouettes as white for shadow map generation.
// Used as a DrawRenderers override material in ShadowCasterPass.
// Transparent pixels are discarded; opaque pixels output solid white.
Shader "Hidden/ShadowCaster2D" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

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
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                clip(alpha - 0.1);
                return float4(1, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
