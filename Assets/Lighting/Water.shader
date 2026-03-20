Shader "Water/WaterSurface" {
    Properties {
        // Surface mask texture (R8, one byte per game pixel):
        //   0   = transparent (no water)
        //   0.5 = interior water
        //   1.0 = surface water (touches open air)
        // Updated by WaterController every simulation tick (every 0.2 s).
        _SurfaceTex      ("Surface Mask", 2D) = "black" {}

        _WaterColorDark  ("Water Dark",   Color) = (0.08, 0.35, 0.85, 0.55)
        _WaterColorLight ("Water Light",  Color) = (0.18, 0.50, 0.95, 0.50)
        _SurfaceColor    ("Surface",      Color) = (1.00, 1.00, 1.00, 0.75)

        // Total game-pixel dimensions of the world (nx*PPT, ny*PPT).
        // Used only to compute per-pixel shimmer phase.
        _WorldPixelSize  ("World Pixel Size XY", Vector) = (1600, 800, 0, 0)
    }

    SubShader {
        Tags {
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass {
            Name "WaterSurface"
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_SurfaceTex);
            SAMPLER(sampler_SurfaceTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _WaterColorDark;
                float4 _WaterColorLight;
                float4 _SurfaceColor;
                float4 _WorldPixelSize;  // (totalPixW, totalPixH, 0, 0)
            CBUFFER_END

            struct Attributes {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target {
                // One texture sample tells us everything — no neighbor checks needed.
                float mask = SAMPLE_TEXTURE2D(_SurfaceTex, sampler_SurfaceTex, IN.uv).r;

                if (mask < 0.25) return half4(0, 0, 0, 0);  // transparent
                if (mask > 0.75) return _SurfaceColor;       // surface highlight

                // Interior water: per-pixel shimmer using game-pixel coordinates.
                // _Time.y == Time.time, so the animation stays frame-rate driven on GPU.
                float px = floor(IN.uv.x * _WorldPixelSize.x);
                float py = floor(IN.uv.y * _WorldPixelSize.y);
                float s = (sin(_Time.y * 1.8 + px * 0.3 + py * 0.5) * 0.5 + 0.5) * 0.4;
                return lerp(_WaterColorDark, _WaterColorLight, s);
            }

            ENDHLSL
        }
    }
}
