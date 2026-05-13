Shader "Water/WaterUnderlay" {
    // Solid base layer that sits behind the BackgroundTile cave wall and occludes
    // the parallax sky painting where water exists. Renders the same alpha shape
    // as Water.shader (sampling the same _SurfaceTex), but with no shimmer and no
    // sparkles — those visuals come from the front Water.shader pass at
    // sortingOrder -5. See SPEC-rendering.md §Water Rendering and the Context
    // section of plans/calm-booping-kitten.md.
    Properties {
        // Same R8 mask uploaded by WaterController each tick. Underlay reuses the
        // same Texture2D instance so updates flow to both materials automatically.
        _SurfaceTex ("Surface Mask",     2D)    = "black" {}
        // Same per-tile tint texture used by Water.shader. When tint.a > 0.5 we
        // use tint.rgb * 0.85 so the underlay matches the front shader's
        // dark-shimmer color and tinted tanks read cleanly across both layers.
        _TintTex    ("Tint (per-tile)",  2D)    = "black" {}
        _BaseColor  ("Base Water Color", Color) = (0.08, 0.35, 0.85, 0.9)
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
            Name "WaterUnderlay"
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
                float4 _BaseColor;
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
                float mask = SAMPLE_TEXTURE2D(_SurfaceTex, sampler_SurfaceTex, IN.uv).r;
                if (mask < 0.25) discard;

                half4 tint = SAMPLE_TEXTURE2D(_TintTex, sampler_TintTex, IN.uv);
                half3 rgb  = (tint.a > 0.5) ? (tint.rgb * 0.85) : _BaseColor.rgb;
                return half4(rgb, _BaseColor.a);
            }

            ENDHLSL
        }

        // UniversalForward fallback — same body. Needed so the underlay still
        // renders under URP's Universal renderer (its transparent queue invokes
        // UniversalForward / SRPDefaultUnlit, not Universal2D).
        Pass {
            Name "WaterUnderlayForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_SurfaceTex);
            SAMPLER(sampler_SurfaceTex);
            TEXTURE2D(_TintTex);
            SAMPLER(sampler_TintTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
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
                float mask = SAMPLE_TEXTURE2D(_SurfaceTex, sampler_SurfaceTex, IN.uv).r;
                if (mask < 0.25) discard;

                half4 tint = SAMPLE_TEXTURE2D(_TintTex, sampler_TintTex, IN.uv);
                half3 rgb  = (tint.a > 0.5) ? (tint.rgb * 0.85) : _BaseColor.rgb;
                return half4(rgb, _BaseColor.a);
            }

            ENDHLSL
        }
    }
    Fallback "Sprites/Default"
}
