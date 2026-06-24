Shader "Water/MoltenGlow" {
    // Colour render for hot decorative liquid (the foundry's molten metal): the metal's per-tile tint
    // with a subtle heat shimmer. This is only the COLOUR — the brightness comes from the emission pass:
    // WaterController registers this sprite as a LightSource emitter with the same mask bound as its
    // _EmissionMap, so EmissionWriter saturates the lightmap at molten pixels and the composite then
    // shows this colour at full brightness, CONSTANT day/night (not lit/dimmed by ambient or point
    // lights) — exactly like a torch flame. Lives on the `Glow` Unity layer (main camera, in-world sort)
    // so mice in front occlude it, and EmissionWriter's sort-mask drops the emission behind them too.
    // For emissive zones the lit decorative-water fill is suppressed (WaterController) — this IS the
    // molten visual. Casts no world light (that's the separate firebox LightSource). See SPEC-rendering.
    Properties {
        // Fill mask: alpha = molten present (Alpha8, WaterController._emissiveTex). Named _SurfaceTex
        // to match the water materials' texture-binding code.
        _SurfaceTex     ("Fill Mask", 2D) = "black" {}
        // Per-tile tint (RGBA32) — the molten metal's liquidColor (shared _TintTex). alpha>0.5 = metal.
        _TintTex        ("Tint (per-tile)", 2D) = "black" {}
        _GlowStrength   ("Body Opacity", Range(0,1)) = 0.92
        _WorldPixelSize ("World Pixel Size XY", Vector) = (1600, 800, 0, 0)
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
            Name "MoltenGlow2D"
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_SurfaceTex); SAMPLER(sampler_SurfaceTex);
            TEXTURE2D(_TintTex);    SAMPLER(sampler_TintTex);

            CBUFFER_START(UnityPerMaterial)
                float  _GlowStrength;
                float4 _WorldPixelSize;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target {
                float mask = SAMPLE_TEXTURE2D(_SurfaceTex, sampler_SurfaceTex, IN.uv).a;
                if (mask < 0.25) return half4(0, 0, 0, 0);              // no molten here
                half4 tint = SAMPLE_TEXTURE2D(_TintTex, sampler_TintTex, IN.uv);
                if (tint.a < 0.5) return half4(0, 0, 0, 0);            // only tinted (metal) tiles

                // Shimmer between the tint and a slightly darker shade (mirrors Water.shader).
                float px = floor(IN.uv.x * _WorldPixelSize.x);
                float py = floor(IN.uv.y * _WorldPixelSize.y);
                float s  = (sin(_Time.y * 1.8 + px * 0.3 + py * 0.5) * 0.5 + 0.5) * 0.4;
                half3 body = lerp(tint.rgb * 0.85, tint.rgb, s);
                return half4(body, _GlowStrength);
            }
            ENDHLSL
        }

        // UniversalForward fallback — same body (URP's transparent queue may invoke this instead).
        Pass {
            Name "MoltenGlowForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_SurfaceTex); SAMPLER(sampler_SurfaceTex);
            TEXTURE2D(_TintTex);    SAMPLER(sampler_TintTex);

            CBUFFER_START(UnityPerMaterial)
                float  _GlowStrength;
                float4 _WorldPixelSize;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target {
                float mask = SAMPLE_TEXTURE2D(_SurfaceTex, sampler_SurfaceTex, IN.uv).a;
                if (mask < 0.25) return half4(0, 0, 0, 0);
                half4 tint = SAMPLE_TEXTURE2D(_TintTex, sampler_TintTex, IN.uv);
                if (tint.a < 0.5) return half4(0, 0, 0, 0);

                float px = floor(IN.uv.x * _WorldPixelSize.x);
                float py = floor(IN.uv.y * _WorldPixelSize.y);
                float s  = (sin(_Time.y * 1.8 + px * 0.3 + py * 0.5) * 0.5 + 0.5) * 0.4;
                half3 body = lerp(tint.rgb * 0.85, tint.rgb, s);
                return half4(body, _GlowStrength);
            }
            ENDHLSL
        }
    }
    Fallback "Sprites/Default"
}
