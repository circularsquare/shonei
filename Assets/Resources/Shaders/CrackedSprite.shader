// Sprite shader for broken / damaged structures. Composites a tileable crack
// texture on top of the base sprite, masked by the base sprite's own alpha so
// cracks only appear on the visible shape of the building (not in transparent
// gaps). URP 2D-native — follows TileSprite.shader's structure so broken
// buildings continue to participate in the project's NormalsCapture +
// LightComposite pipeline (the "LightMode" = "Universal2D" tag on the pass is
// what makes the renderer show up in those filters).
//
// Properties:
//   _MainTex       — base sprite (Unity auto-binds from SpriteRenderer.sprite)
//   _CrackTex      — tileable grayscale crack atlas. Alpha drives crack density.
//                    Set texture wrap mode to Repeat.
//   _CrackColor    — tint multiplied into the crack RGB (default near-black)
//   _CrackStrength — 0..1 overall crack opacity
//   _CrackScale    — world-unit tile rate (world-space UVs so cracks don't
//                    stretch with building size)
//   _BrokenTint    — RGB multiplier applied to the base sprite so broken
//                    buildings desaturate even before crack compositing.
Shader "Custom/CrackedSprite" {
    Properties {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _CrackTex      ("Crack Texture", 2D) = "black" {}
        _CrackColor    ("Crack Color",    Color) = (0.08, 0.06, 0.05, 1)
        _CrackStrength ("Crack Strength", Range(0,1)) = 0.9
        _CrackScale    ("Crack Scale (world)", Float) = 0.5
        _BrokenTint    ("Broken Tint",    Color) = (0.75, 0.75, 0.75, 1)
        [HideInInspector] _Color         ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
    }

    SubShader {
        Tags {
            "Queue"        = "Transparent"
            "RenderType"   = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        // URP 2D pass — tagged Universal2D so LightFeature's NormalsCapture pass
        // still picks up this renderer (the capture uses an override material,
        // so the contents of this pass don't get drawn during the capture — the
        // tag is what matters for filtering).
        Pass {
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);  SAMPLER(sampler_MainTex);
            TEXTURE2D(_CrackTex); SAMPLER(sampler_CrackTex);
            float4 _CrackTex_ST;
            float4 _CrackColor;
            float  _CrackStrength;
            float  _CrackScale;
            float4 _BrokenTint;
            float4 _Color;
            half4  _RendererColor;

            struct Attributes {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                float2 worldUV    : TEXCOORD1;
            };

            Varyings vert(Attributes v) {
                Varyings o;
                float3 wp    = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(wp);
                o.uv         = v.uv;
                o.worldUV    = wp.xy * _CrackScale * _CrackTex_ST.xy + _CrackTex_ST.zw;
                o.color      = v.color * _Color * _RendererColor;
                return o;
            }

            half4 frag(Varyings i) : SV_Target {
                half4 base = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;
                clip(base.a - 0.01);
                base.rgb *= _BrokenTint.rgb;

                half4 crack = SAMPLE_TEXTURE2D(_CrackTex, sampler_CrackTex, i.worldUV);
                float mask  = crack.a * base.a * _CrackStrength;

                half3 outRgb = lerp(base.rgb, _CrackColor.rgb, mask);
                return half4(outRgb, base.a);
            }
            ENDHLSL
        }

        // UniversalForward fallback — same body, needed so the shader still
        // renders if URP 2D's 2D renderer isn't active on the camera.
        Pass {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);  SAMPLER(sampler_MainTex);
            TEXTURE2D(_CrackTex); SAMPLER(sampler_CrackTex);
            float4 _CrackTex_ST;
            float4 _CrackColor;
            float  _CrackStrength;
            float  _CrackScale;
            float4 _BrokenTint;
            float4 _Color;
            half4  _RendererColor;

            struct Attributes {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                float2 worldUV    : TEXCOORD1;
            };

            Varyings vert(Attributes v) {
                Varyings o;
                float3 wp    = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(wp);
                o.uv         = v.uv;
                o.worldUV    = wp.xy * _CrackScale * _CrackTex_ST.xy + _CrackTex_ST.zw;
                o.color      = v.color * _Color * _RendererColor;
                return o;
            }

            half4 frag(Varyings i) : SV_Target {
                half4 base = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;
                clip(base.a - 0.01);
                base.rgb *= _BrokenTint.rgb;

                half4 crack = SAMPLE_TEXTURE2D(_CrackTex, sampler_CrackTex, i.worldUV);
                float mask  = crack.a * base.a * _CrackStrength;

                half3 outRgb = lerp(base.rgb, _CrackColor.rgb, mask);
                return half4(outRgb, base.a);
            }
            ENDHLSL
        }
    }

    Fallback "Sprites/Default"
}
