// UI variant of the mouse fur recolor — used by the MouseHeadIcon portrait widget so the
// UI head reflects a mouse's actual fur color. Structurally identical to Unity's built-in
// UI/Default (full clip-rect + stencil/mask support, so it works inside ScrollRects and
// RectMask2D), with one change: instead of multiplying the texture by the vertex color, it
// runs RemapFur using the vertex color as the per-mouse fur target. MouseHeadIcon sets
// Image.color = the fur color, which the Canvas feeds in as vertex color — so every icon
// shares ONE material and still batches; only the vertex stream differs.
//
// Keep the 5 source shades + offset logic in sync with Custom/Sprite (Sprite.shader); the
// two are intentionally duplicated (world SpriteRenderer pipeline vs. Canvas UI pipeline).
Shader "Custom/MouseHeadUI"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            // ── Per-mouse fur recolor (mirror of Sprite.shader's RemapFur) ──────
            static const half3 FUR_MAIN = half3(145.0, 152.0, 156.0) / 255.0;
            static const half3 FUR_SRC[5] = {
                half3(145.0, 152.0, 156.0) / 255.0,  // main
                half3(154.0, 160.0, 164.0) / 255.0,  // highlight
                half3(137.0, 142.0, 145.0) / 255.0,  // shadow
                half3(112.0, 118.0, 121.0) / 255.0,  // eep deep
                half3(106.0, 113.0, 116.0) / 255.0   // eep deepest
            };
            half3 RemapFur(half3 t, half3 fur)
            {
                [unroll] for (int k = 0; k < 5; k++)
                    if (all(abs(t - FUR_SRC[k]) < 1.5 / 255.0))
                        return fur + (FUR_SRC[k] - FUR_MAIN);
                return t;
            }

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                half4 tex = tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd;
                // Recolor the fur shades to the vertex-color (= Image.color) fur tone; keep
                // the vertex color's alpha for fades / CanvasGroup. Eyes and pink ears pass
                // through untouched, same as the in-world mouse.
                half4 color = half4(RemapFur(tex.rgb, IN.color.rgb), tex.a * IN.color.a);

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif

                return color;
            }
            ENDCG
        }
    }
}
