// Fullscreen pass: writes spatially-varying ambient light into the light RT.
// Samples _SkyExposureTex (per-tile exposure, 0 underground / 1 sky) and
// outputs _AmbientColor * exposure. Max blend against the cave-ambient-cleared RT,
// so sky light competes with (rather than stacks on) the constant cave floor.
//
// Used by LightPass in place of a uniform ambient clear, so underground areas
// receive no ambient light while the surface stays fully lit.
Shader "Hidden/LightAmbientFill" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        BlendOp Max
        Blend One One
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "AmbientFill"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SkyExposure.hlsl"

            float4 _AmbientColor;

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
                float exposure = SampleSkyExposure(IN.uv);
                return float4(_AmbientColor.rgb * exposure, 1.0);
            }
            ENDHLSL
        }
    }
}
