// Unlit transparent sprite shader for selection / highlight / harvest overlays
// that live on the Unlit layer (drawn after the lighting composite by
// UnlitOverlayCamera — see SPEC-rendering.md §Sky / background). Identical to a
// plain unlit sprite (texture × vertex color × renderer color × _Color) EXCEPT
// it multiplies in a global half-strength ambient tint `_OverlayAmbient`,
// broadcast every frame by SunController. Effect: these overlays settle toward
// the night ambient so their bright colors stop glaring in the dark, while
// staying full-bright in daylight (where ambient ≈ white).
//
// Single UniversalForward pass on purpose: with no Universal2D pass,
// NormalsCapture never sees these sprites — they must NOT participate in the
// lighting pipeline (the global tint IS their only "lighting").
Shader "Custom/UnlitOverlayAmbient" {
    Properties {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [HideInInspector] _Color ("Tint", Color) = (1,1,1,1)
        // Auto-injected per-renderer by SpriteRenderer via MPB. MUST sit outside
        // UnityPerMaterial — an MPB write to a CBUFFER property opts the renderer
        // out of SRP Batching (same rule as Custom/Sprite).
        [HideInInspector] [PerRendererData] _RendererColor ("RendererColor", Color) = (1,1,1,1)
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
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END
            // Per-renderer (SpriteRenderer MPB) — outside UnityPerMaterial.
            half4 _RendererColor;
            // Global half-ambient tint, set by SunController each frame. Outside
            // the CBUFFER (SRP batcher rejects globals inside per-material
            // cbuffers). Defaults to 0 until set, so SpriteMaterialUtil seeds it
            // white at startup before any overlay can render black.
            half4 _OverlayAmbient;

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
                c.rgb *= _OverlayAmbient.rgb;  // half-ambient dim — alpha untouched
                return c;
            }
            ENDHLSL
        }
    }
    Fallback "Sprites/Default"
}
