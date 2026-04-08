// Custom tile sprite shader with jagged-edge clipping on exposed surfaces.
// Uses the same TileEdgeClip() as NormalsCapture.shader so clipped pixels
// are identical in both passes (no ghost normals or lighting artifacts).
//
// _AdjacencyMask (float 0–15) is set per tile via MaterialPropertyBlock.
// Default 15 = all neighbours solid = no clipping.
Shader "Custom/TileSprite" {
    Properties {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        [HideInInspector] _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        _AdjacencyMask ("Adjacency Mask", Float) = 15
    }
    SubShader {
        Tags {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
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
            #include "Assets/Lighting/TileEdge.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;
            float4 _Color;
            half4  _RendererColor;
            float  _AdjacencyMask;

            struct Attributes {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                float2 worldPos   : TEXCOORD1;
            };

            Varyings vert(Attributes v) {
                Varyings o;
                float3 wp    = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(wp);
                o.uv         = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldPos   = wp.xy;
                o.color      = v.color * _Color * _RendererColor;
                return o;
            }

            half4 frag(Varyings i) : SV_Target {
                // Jagged edge clip — must match NormalsCapture exactly.
                if (!TileEdgeClip(_AdjacencyMask, i.worldPos))
                    discard;

                float4 c = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                clip(c.a - 0.1);
                return c;
            }
            ENDHLSL
        }

        // UniversalForward fallback — same clip logic.
        Pass {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Lighting/TileEdge.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;
            float4 _Color;
            half4  _RendererColor;
            float  _AdjacencyMask;

            struct Attributes {
                float3 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                float2 worldPos   : TEXCOORD1;
            };

            Varyings vert(Attributes v) {
                Varyings o;
                float3 wp    = TransformObjectToWorld(v.positionOS);
                o.positionCS = TransformWorldToHClip(wp);
                o.uv         = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldPos   = wp.xy;
                o.color      = v.color * _Color * _RendererColor;
                return o;
            }

            half4 frag(Varyings i) : SV_Target {
                if (!TileEdgeClip(_AdjacencyMask, i.worldPos))
                    discard;

                float4 c = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                clip(c.a - 0.1);
                return c;
            }
            ENDHLSL
        }
    }
    Fallback "Sprites/Default"
}
