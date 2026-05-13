// Plant-only lit-sprite shader. Identical fragment behaviour to Custom/Sprite,
// but the vertex stage applies wind sway in world space — bottom of plant
// stays anchored, top sways most. Multi-tile plants (e.g. bamboo) bend
// continuously across tile boundaries because every SR of one plant shares
// _PlantBaseY and _PlantHeight, so adjacent tile vertices compute identical
// world-space offsets.
//
// Per-renderer MPB (written by SpriteMaterialUtil.SetPlantSwayMPB on plant
// construction / extension claim / growth):
//   _PlantBaseY  — world Y of the plant's anchor tile. Same for every SR of
//                  one plant. Sway weight ramps up from this Y.
//   _PlantHeight — current tile-height of the plant (1 for grass / juvenile
//                  bamboo; 3 for full-grown bamboo). Normalises sway so the
//                  top tip of any species reaches the same peak amplitude.
//   _PlantPhase  — small per-instance phase offset, derived from world coords.
//                  Adds variation between adjacent plants without per-frame
//                  cost.
//   _PlantSway   — gate flag (0 = no sway, 1 = sway). Used by Phase 2's
//                  matching change in NormalsCapture.shader; harmless here
//                  (PlantSprite.shader is only assigned to plant SRs anyway,
//                  but reading the flag keeps both passes symmetrical).
//
// Globals (set by WindShaderController):
//   _Wind         — current wind scalar, [-1, 1] typical. Positive = right.
//   _SwayLean     — wind-driven steady lean, world units per unit-wind at
//                   weight=1. ~0.08 → ~1.3 px lean at full wind.
//   _SwayGust     — wind-driven oscillation amplitude on top of lean.
//   _SwayAmbient  — still-air idle sway amplitude.
//   _SwayFreq     — base oscillation frequency in rad/sec.
//
// Why a separate shader (not just a flag in Custom/Sprite): keeps the sway
// math out of every other lit sprite's vertex stage, and gives Phase 3
// (per-pixel sway masks for trees) somewhere to grow without disturbing the
// other lit-sprite path.
//
// Dual-pass (Universal2D + UniversalForward) per the project convention —
// see SPEC-rendering.md §URP setup. Fallback to Sprites/Default for safety.
Shader "Custom/PlantSprite" {
    Properties {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [HideInInspector] _Color         ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        // Default white so unmasked plants would behave as full-sway in
        // mask-mode; in practice _UseMask gates the sample so this is only
        // a safety net when the secondary texture is missing.
        [PerRendererData] _SwayMask ("Sway Mask", 2D) = "white" {}
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

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Sway.hlsl"

        TEXTURE2D(_MainTex);  SAMPLER(sampler_MainTex);
        TEXTURE2D(_SwayMask); SAMPLER(sampler_SwayMask);

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
            float3 worldPos = TransformObjectToWorld(v.positionOS);
            // _UseMask = 0:  regular plant. Per-vertex weighted bend.
            // _UseMask = 1:  flower with `_sway` mask, two SRs per instance.
            //   _RoleIsHead = 0 (stem SR): same per-vertex bend; frag discards
            //                              the mask>0.5 half so only stem pixels
            //                              survive.
            //   _RoleIsHead = 1 (head SR): every vertex shifts uniformly by the
            //                              amount at _HeadCenterY — the head
            //                              reads as a rigid chunk whose anchor
            //                              follows the stem-top below.
            // PlantVertexShift() in Sway.hlsl picks the right formula based on
            // _RoleIsHead so the two shaders (this + NormalsCapture) can't
            // disagree on the math.
            if (_PlantSway > 0.5) {
                worldPos.x += PlantVertexShift(worldPos.y);
            }
            o.positionCS = TransformWorldToHClip(worldPos);
            o.uv         = v.uv;
            o.color      = v.color * _Color * _RendererColor;
            return o;
        }

        half4 frag(Varyings i) : SV_Target {
            // Mask-discard mode: stem and head SRs share the same texture and
            // mask; each keeps only its own half so they don't double-draw
            // wherever the masks overlap. Pixels at the boundary fall into
            // whichever side wins the > 0.5 split.
            if (_UseMask > 0.5) {
                float mask = SAMPLE_TEXTURE2D(_SwayMask, sampler_SwayMask, i.uv).r;
                if ((mask > 0.5) != (_RoleIsHead > 0.5)) discard;
            }
            half4 c = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
            clip(c.a - 0.01);
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
