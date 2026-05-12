// Chunked tile body / overlay / snow shader. One material per layer
// (body / overlay / snow), shared across all chunks of that layer.
// Each chunked MeshRenderer binds its layer's Texture2DArray via MPB:
//   _MainTexArr  — body sprites, overlay sprites, or snow sprites
//   _SortBucket  — written by LightReceiverUtil.SetSortBucket on the MeshRenderer
//
// Vertex layout (per quad corner):
//   POSITION    — chunk-local XY, Z=0
//   TEXCOORD0   — atlas-slice UV, 0..1
//   TEXCOORD1.x — slice index into _MainTexArr (body/overlay/snow slice)
//   TEXCOORD1.y — slice index into _NormalArr (only sampled by ChunkedNormalsCapture)
//
// Two passes: Universal2D for the URP 2D Renderer; UniversalForward for the
// URP Universal Renderer (current project setup). Both sample the array at
// the per-vertex slice index passed through TEXCOORD0/1.
Shader "Custom/ChunkedTileSprite" {
    Properties {
        [HideInInspector] _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
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

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D_ARRAY(_MainTexArr); SAMPLER(sampler_MainTexArr);

        CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            half4  _RendererColor;
        CBUFFER_END

        struct Attributes {
            float3 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
            float2 sliceUV    : TEXCOORD1;
        };

        struct Varyings {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
            float  slice      : TEXCOORD1;
        };

        Varyings vert(Attributes v) {
            Varyings o;
            o.positionCS = TransformWorldToHClip(TransformObjectToWorld(v.positionOS));
            o.uv         = v.uv;
            o.slice      = v.sliceUV.x;
            return o;
        }

        half4 frag(Varyings i) : SV_Target {
            half4 c = SAMPLE_TEXTURE2D_ARRAY(_MainTexArr, sampler_MainTexArr, i.uv, i.slice);
            c *= _Color * _RendererColor;
            clip(c.a - 0.1);
            return c;
        }
        ENDHLSL

        Pass {
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }

        Pass {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
    Fallback "Sprites/Default"
}
