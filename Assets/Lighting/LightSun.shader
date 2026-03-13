// Full-screen directional light pass for the sun.
// Samples _CapturedNormalsRT and computes NdotL against the sun direction.
// Additive blend: sun highlights add on top of the final point-light RT.
Shader "Hidden/LightSun" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        BlendOp Add
        Blend One One
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "LightSun"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _SunColor;
            float  _SunIntensity;
            float3 _SunDir;    // XY world direction toward sun (Z = 0)
            float  _SunHeight; // controls how much sun hits flat/camera-facing surfaces
            float  _AmbientNormal;

            // Populated each frame by NormalsCapturePass.
            TEXTURE2D(_CapturedNormalsRT);
            SAMPLER(sampler_CapturedNormalsRT);

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
                // No Y-flip: see LightCircle.shader for explanation.
                float2 screenUV = IN.positionCS.xy / _ScreenParams.xy;
                float4 ns = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, screenUV);

                // Black = no sprite here; use flat camera-facing normal as fallback.
                if (dot(ns.rgb, ns.rgb) < 0.01) ns = float4(0.5, 0.5, 0.0, 1.0);

                float3 normal  = normalize(ns.rgb * 2.0 - 1.0);
                float3 sunDir3 = normalize(float3(_SunDir.xy, -_SunHeight));
                float  ndotl   = max(_AmbientNormal, dot(normal, sunDir3));

                return float4(saturate(_SunColor.rgb * (_SunIntensity * ndotl)), 1.0);
            }
            ENDHLSL
        }
    }
}
