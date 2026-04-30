// Project-wide replacement for Unity's Sprite-Lit-Default. Identical visible
// output to a plain unlit sprite (texture × vertex color × renderer color),
// but with both Universal2D and UniversalForward passes so it works under both
// URP renderer types AND participates in our LightFeature's NormalsCapture
// filter (which keys on the Universal2D LightMode tag).
//
// Why we don't use Sprite-Lit-Default: it has only a Universal2D pass, so it
// disappears under URP's Universal renderer. We don't need its 2D-Lights
// sampling either — our custom LightFeature does all lighting via a separate
// multiply blit, and there are no Light2D components in the scene.
Shader "Custom/Sprite" {
    Properties {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [HideInInspector] _Color         ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
    }
    SubShader {
        Tags {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass {
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                half4  _RendererColor;
            CBUFFER_END

            struct Attributes {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes v) {
                Varyings o;
                o.positionCS = TransformWorldToHClip(TransformObjectToWorld(v.positionOS));
                o.uv         = v.uv;
                o.color      = v.color * _Color * _RendererColor;
                return o;
            }

            half4 frag(Varyings i) : SV_Target {
                half4 c = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                clip(c.a - 0.01);
                return c;
            }
            ENDHLSL
        }

        Pass {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                half4  _RendererColor;
            CBUFFER_END

            struct Attributes {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes v) {
                Varyings o;
                o.positionCS = TransformWorldToHClip(TransformObjectToWorld(v.positionOS));
                o.uv         = v.uv;
                o.color      = v.color * _Color * _RendererColor;
                return o;
            }

            half4 frag(Varyings i) : SV_Target {
                half4 c = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                clip(c.a - 0.01);
                return c;
            }
            ENDHLSL
        }
    }
    Fallback "Sprites/Default"
}
