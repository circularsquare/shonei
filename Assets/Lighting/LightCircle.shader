// Draws a soft radial gradient for one point light source, modulated by NdotL
// from _CapturedNormalsRT (world-space normals rendered by NormalsCapturePass).
// Screen blend: overlapping sources accumulate softly without blowing out.
Shader "Hidden/LightCircle" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        BlendOp Add
        Blend One OneMinusSrcColor
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "LightCircle"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Set per-draw via MaterialPropertyBlock.
            float4 _LightColor;
            float  _Intensity;
            float  _InnerFraction; // innerRadius / outerRadius  (0–1)
            float4 _LightWorldPos; // world-space XY of the light source
            float  _LightHeight;   // Z offset above sprite plane for NdotL angle
            float  _AmbientNormal; // minimum NdotL floor (softens back-face darkness)

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
                float2 worldPos   : TEXCOORD1;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                float3 worldPos3 = TransformObjectToWorld(IN.positionOS);
                OUT.positionCS   = TransformWorldToHClip(worldPos3);
                OUT.uv           = IN.uv;
                OUT.worldPos     = worldPos3.xy;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                // Radial falloff.
                float2 d       = IN.uv - 0.5;
                float  r       = length(d);
                float  inner   = _InnerFraction * 0.5;
                float  falloff = 1.0 - smoothstep(inner, 0.5, r);
                falloff = falloff * falloff;

                // Sample world-space normals RT at this fragment's screen position.
                // No Y-flip: DrawRenderers writes _CapturedNormalsRT in OpenGL convention
                // (V=0 at bottom). positionCS.y is also 0 at the bottom (renderIntoTexture:false
                // projection used by LightPass), so they match directly. Flipping here
                // displaces normals vertically and inverts the Y lighting direction.
                float2 screenUV = IN.positionCS.xy / _ScreenParams.xy;
                float4 ns = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, screenUV);

                // Black = no sprite here, use flat fallback (0,0,-1).
                if (dot(ns.rgb, ns.rgb) < 0.01) ns = float4(0.5, 0.5, 0.0, 1.0);

                float3 normal  = normalize(ns.rgb * 2.0 - 1.0);
                float3 toLight = normalize(float3(_LightWorldPos.xy - IN.worldPos.xy, -_LightHeight));
                float  ndotl   = max(_AmbientNormal, dot(normal, toLight));

                return float4(saturate(_LightColor.rgb * (_Intensity * falloff * ndotl)), 1.0);
            }
            ENDHLSL
        }
    }
}
