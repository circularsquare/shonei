// Full-screen directional light pass for the sun.
// Samples _CapturedNormalsRT and computes NdotL against the sun direction.
// Modulated by _SkyExposureTex so the sun fades out underground.
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
            #include "SkyExposure.hlsl"

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

                // Alpha tier: 1.0 = solid tile, 0.5 = lit-only, 0.0 = no sprite.
                // (isCaster was used by shadow ray march — commented out below.)
                float hasSprite = ns.a > 0.25 ? 1.0 : 0.0;

                // Black = no sprite here; use flat camera-facing normal as fallback.
                if (hasSprite < 0.5) ns = float4(0.5, 0.5, 0.0, 1.0);

                float3 normal  = normalize(ns.rgb * 2.0 - 1.0);
                float3 sunDir3 = normalize(float3(_SunDir.xy, -_SunHeight));
                float  ndotl   = max(_AmbientNormal, dot(normal, sunDir3));

                // Shadow ray march disabled for performance. Uncomment to re-enable:
                // float inShadow = 0.0;
                // [branch] if (_ShadowLength > 0.0) {
                //     float2 shadowDir  = normalize(_SunDir.xy);
                //     float2 shadowStep = shadowDir * _ShadowLength * _WorldToUV * (1.0 / 16.0);
                //     for (int i = 1; i <= 16; i++) {
                //         float2 sampleUV = screenUV + shadowStep * i;
                //         float4 caster = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, sampleUV);
                //         if (caster.a > 0.75) { inShadow = 1.0; break; }
                //     }
                // }
                // float sunFactor = 1.0 - inShadow * (1.0 - isCaster) * _ShadowDarkness;

                // Fade sun with distance from sky — same exposure lookup as LightAmbientFill.
                float exposure = SampleSkyExposure(IN.uv);

                return float4(saturate(_SunColor.rgb * (_SunIntensity * ndotl * exposure)), 1.0);
            }
            ENDHLSL
        }
    }
}
