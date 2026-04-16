Shader "Water/WaterSurface" {
    Properties {
        // Surface mask texture (R8, one byte per game pixel):
        //   0   = transparent (no water)
        //   0.5 = interior water
        //   1.0 = surface water (touches open air)
        // Updated by WaterController every simulation tick (every 0.2 s).
        _SurfaceTex      ("Surface Mask", 2D) = "black" {}

        // Per-tile tint (RGBA32, one texel per world tile). When alpha > 0.5, the tile
        // uses tint.rgb as the shimmer "light" color and tint.rgb * 0.85 as the "dark"
        // color — overriding _WaterColorDark/Light. Used by decorative zones (tanks)
        // to show liquid-specific colors (e.g. soymilk = beige). Point-filtered.
        // Default "black" (alpha=0) means unbound/empty tiles hit the fallback path.
        _TintTex         ("Tint (per-tile)", 2D) = "black" {}

        _WaterColorDark  ("Water Dark",   Color) = (0.08, 0.35, 0.85, 0.55)
        _WaterColorLight ("Water Light",  Color) = (0.18, 0.50, 0.95, 0.50)
        _SurfaceColor    ("Surface",      Color) = (1.00, 1.00, 1.00, 0.75)

        // Total game-pixel dimensions of the world (nx*PPT, ny*PPT).
        // Used only to compute per-pixel shimmer phase.
        _WorldPixelSize  ("World Pixel Size XY", Vector) = (1600, 800, 0, 0)
        _SparkleWidth    ("Sparkle Width (px)", Float) = 2.0
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
            TEXTURE2D(_TintTex);
            SAMPLER(sampler_TintTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _WaterColorDark;
                float4 _WaterColorLight;
                float4 _SurfaceColor;
                float4 _WorldPixelSize;  // (totalPixW, totalPixH, 0, 0)
                float  _SparkleWidth;
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

            // Cheap pseudo-random hash: maps a 2D cell coordinate to 0–1.
            float hash(float2 p) {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            half4 frag(Varyings IN) : SV_Target {
                // One texture sample tells us everything — no neighbor checks needed.
                float mask = SAMPLE_TEXTURE2D(_SurfaceTex, sampler_SurfaceTex, IN.uv).r;

                if (mask < 0.25) return half4(0, 0, 0, 0);  // transparent
                if (mask > 0.75) return _SurfaceColor;       // surface highlight

                // Per-tile tint: decorative zones stamp a liquid's color (alpha=255) into
                // _TintTex; everything else is alpha=0 and falls through to the shader's
                // default water blue. Dark shimmer = light × 0.85 (15% darker).
                half4 tint = SAMPLE_TEXTURE2D(_TintTex, sampler_TintTex, IN.uv);
                half4 lightCol;
                half4 darkCol;
                if (tint.a > 0.5) {
                    lightCol = half4(tint.rgb,         _WaterColorLight.a);
                    darkCol  = half4(tint.rgb * 0.85,  _WaterColorDark.a);
                } else {
                    lightCol = _WaterColorLight;
                    darkCol  = _WaterColorDark;
                }

                // Interior water: per-pixel shimmer using game-pixel coordinates.
                // _Time.y == Time.time, so the animation stays frame-rate driven on GPU.
                float px = floor(IN.uv.x * _WorldPixelSize.x);
                float py = floor(IN.uv.y * _WorldPixelSize.y);
                float s = (sin(_Time.y * 1.8 + px * 0.3 + py * 0.5) * 0.5 + 0.5) * 0.4;
                half4 waterCol = lerp(darkCol, lightCol, s);

                // --- Sparkles: small bright regions that fade in and out ---
                // Divide into cells (~10×1 pixels). Thin horizontal rows.
                float2 cell = floor(float2(px / 10.0, py));
                float cellRand = hash(cell);

                // Only ~15% of cells are sparkle candidates at any moment.
                // Each cell has a unique phase; sparkle activates when wave peaks.
                float sparkleWave = sin(_Time.y * 0.6 + cellRand * 40.0);
                float sparkleActive = smoothstep(0.82, 1.0, sparkleWave);

                // Horizontal streak: ~3px wide, exactly 1px tall (row already locked).
                float localX = px - cell.x * 10.0;
                float centerX = hash(cell + 0.5) * 6.0 + 2.0;
                float inCluster = 1.0 - saturate(abs(localX - centerX) / _SparkleWidth);

                // Per-pixel flicker within the cluster for a lively look.
                float pixelPhase = hash(float2(px, py));
                float flicker = sin(_Time.y * 2.5 + pixelPhase * 20.0) * 0.5 + 0.5;

                float sparkle = sparkleActive * inCluster * flicker;
                return lerp(waterCol, half4(1, 1, 1, waterCol.a), sparkle * 0.7);
            }

            ENDHLSL
        }
    }
}
