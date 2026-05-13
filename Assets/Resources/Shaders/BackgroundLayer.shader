// Parallax background sprite shader. Used by BackgroundLayer.cs.
//
// This shader is *only* the visible-rendering pass; the parallax math
// happens upstream in `Hidden/BackgroundLayerGen`, which bakes a
// viewport-aligned, parallax-shifted view of the user texture into
// an RT each frame. The RT is MPB-bound here as _MainTex, so we just
// sample it at the sprite's native UVs.
//
// Why bake first and sample plainly here: the project's NormalsCapture
// override material samples _MainTex at native sprite UVs (it doesn't
// know about our parallax math). If we computed parallax inline here,
// the visible alpha mask and the captured-normals alpha mask would
// disagree, and the LightComposite path (which treats "sprite-pixel"
// and "sky-pixel" differently — see LightFeature.cs §6) would render
// the un-parallaxed alpha mask as ghost shadows on the sky at times
// when the lightmap and skyLightColor diverge (most visibly at
// sunset / sunrise). Baking into the RT means both passes sample the
// same image and the masks agree.
//
// Tagged `LightMode = Universal2D` so the URP 2D renderer draws this
// and the NormalsCapture override material's filter picks it up. The
// MPB-bound flat normal texture (0.5, 0.5, 1.0) ensures LightSun
// contributes a uniform brightness across the background, so the
// day/night cycle dims the layer through the lightmap composite
// without per-pixel sun shading artifacts. sr.color drives
// _RendererColor on top for explicit tinting.
Shader "Hidden/BackgroundLayer" {
    Properties {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [HideInInspector] _Color         ("Tint",          Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
    }

    SubShader {
        Tags {
            "Queue"          = "Transparent"
            "RenderType"     = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        HLSLINCLUDE
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

        Varyings vert(Attributes IN) {
            Varyings OUT;
            float3 wp     = TransformObjectToWorld(IN.positionOS);
            OUT.positionCS = TransformWorldToHClip(wp);
            OUT.uv         = IN.uv;
            OUT.color      = IN.color * _Color * _RendererColor;
            return OUT;
        }

        half4 frag(Varyings IN) : SV_Target {
            half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
            return tex * IN.color;
        }
        ENDHLSL

        Pass {
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            ENDHLSL
        }

        Pass {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            ENDHLSL
        }
    }

    Fallback "Sprites/Default"
}
