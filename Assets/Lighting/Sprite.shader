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
        // _RendererColor is auto-injected per-renderer by SpriteRenderer
        // via MPB. It MUST sit outside UnityPerMaterial — keeping it in
        // CBUFFER opts every SpriteRenderer out of SRP Batching, since
        // any MPB write to a CBUFFER property disqualifies the renderer.
        [HideInInspector] [PerRendererData] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        // Per-mouse fur tint, set per-renderer via MPB (see AnimationController.ApplyFurColor).
        // Default = the main fur shade (91989c) so the remap is the identity for every sprite
        // that never gets a per-renderer value — i.e. everything that isn't a mouse body part.
        [HideInInspector] [PerRendererData] _FurColor ("Fur Color", Color) = (0.5686275,0.5960784,0.6117647,1)
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
            CBUFFER_END
            // Outside CBUFFER — SpriteRenderer writes these per-renderer via MPB.
            half4 _RendererColor;
            half4 _FurColor;

            // ── Per-mouse fur recolor ─────────────────────────────────────────
            // Remaps the 5 known cool-gray fur shades to _FurColor, preserving each
            // shade's original per-channel offset from the main shade — so authoring
            // just the new main color in JSON reconstructs its highlight/shadow/eep
            // shades. Every other pixel (eyes = pure black/white, pink paws/ears, and
            // all non-mouse sprites) passes through untouched. Exact-match keying is
            // safe: gamma color space + Point-filtered uncompressed atlas means texels
            // arrive as their authored sRGB values. _FurColor defaults to the main
            // shade, making this the identity for any renderer without a per-renderer
            // value. (5 source colors: highlight 9aa0a4, main 91989c, shadow 898e91,
            // eep 707679, eep 6a7174 — main subtracted out as the offset origin.)
            static const half3 FUR_MAIN = half3(145.0, 152.0, 156.0) / 255.0;
            static const half3 FUR_SRC[5] = {
                half3(145.0, 152.0, 156.0) / 255.0,  // main
                half3(154.0, 160.0, 164.0) / 255.0,  // highlight
                half3(137.0, 142.0, 145.0) / 255.0,  // shadow
                half3(112.0, 118.0, 121.0) / 255.0,  // eep deep
                half3(106.0, 113.0, 116.0) / 255.0   // eep deepest
            };
            half3 RemapFur(half3 t, half3 fur) {
                [unroll] for (int k = 0; k < 5; k++)
                    if (all(abs(t - FUR_SRC[k]) < 1.5 / 255.0))
                        return fur + (FUR_SRC[k] - FUR_MAIN);
                return t;
            }

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
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                tex.rgb = RemapFur(tex.rgb, _FurColor.rgb);
                half4 c = i.color * tex;
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
            CBUFFER_END
            // Outside CBUFFER — SpriteRenderer writes these per-renderer via MPB.
            half4 _RendererColor;
            half4 _FurColor;

            // ── Per-mouse fur recolor ─────────────────────────────────────────
            // Remaps the 5 known cool-gray fur shades to _FurColor, preserving each
            // shade's original per-channel offset from the main shade — so authoring
            // just the new main color in JSON reconstructs its highlight/shadow/eep
            // shades. Every other pixel (eyes = pure black/white, pink paws/ears, and
            // all non-mouse sprites) passes through untouched. Exact-match keying is
            // safe: gamma color space + Point-filtered uncompressed atlas means texels
            // arrive as their authored sRGB values. _FurColor defaults to the main
            // shade, making this the identity for any renderer without a per-renderer
            // value. (5 source colors: highlight 9aa0a4, main 91989c, shadow 898e91,
            // eep 707679, eep 6a7174 — main subtracted out as the offset origin.)
            static const half3 FUR_MAIN = half3(145.0, 152.0, 156.0) / 255.0;
            static const half3 FUR_SRC[5] = {
                half3(145.0, 152.0, 156.0) / 255.0,  // main
                half3(154.0, 160.0, 164.0) / 255.0,  // highlight
                half3(137.0, 142.0, 145.0) / 255.0,  // shadow
                half3(112.0, 118.0, 121.0) / 255.0,  // eep deep
                half3(106.0, 113.0, 116.0) / 255.0   // eep deepest
            };
            half3 RemapFur(half3 t, half3 fur) {
                [unroll] for (int k = 0; k < 5; k++)
                    if (all(abs(t - FUR_SRC[k]) < 1.5 / 255.0))
                        return fur + (FUR_SRC[k] - FUR_MAIN);
                return t;
            }

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
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                tex.rgb = RemapFur(tex.rgb, _FurColor.rgb);
                half4 c = i.color * tex;
                clip(c.a - 0.01);
                return c;
            }
            ENDHLSL
        }
    }
    Fallback "Sprites/Default"
}
