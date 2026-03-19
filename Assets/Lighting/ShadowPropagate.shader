// Cascaded shadow propagation shader — O(log N) passes to build a shadow map.
//
// Pass 0 (Seed):      reads _CapturedNormalsRT, outputs 1 where a sprite pixel exists.
// Pass 1 (Propagate): reads _ShadowSrc, outputs max(self, self + _ShadowStepDir).
//
// Usage (see LightPass):
//   Seed once into ping-pong RT A.
//   For k = 0..numPasses: propagate from A→B (or B→A), doubling _ShadowStepDir each pass.
//   After ceil(log2(shadowLengthPx)) passes, the result is a full shadow map.
//   Expose the final RT as _ShadowRT for LightSun to sample.
Shader "Hidden/ShadowPropagate" {
    SubShader {
        ZWrite Off ZTest Always Cull Off Blend Off

        // ── Pass 0: Seed ────────────────────────────────────────────────────────
        // Reads the normals RT (already a global); outputs 1 where a sprite exists.
        Pass {
            Name "ShadowSeed"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag_seed
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_CapturedNormalsRT); SAMPLER(sampler_CapturedNormalsRT);

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS);
                return OUT;
            }

            float4 frag_seed(Varyings IN) : SV_Target {
                // Same UV convention as LightSun / LightCircle — no Y-flip needed.
                float2 uv = IN.positionCS.xy / _ScreenParams.xy;
                float4 ns = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, uv);
                float occupied = dot(ns.rgb, ns.rgb) > 0.01 ? 1.0 : 0.0;
                return float4(occupied, 0, 0, 1);
            }
            ENDHLSL
        }

        // ── Pass 1: Propagate ───────────────────────────────────────────────────
        // Reads _ShadowSrc; outputs max(self, neighbour at _ShadowStepDir).
        // Each pass doubles the shadow reach; after K passes covers 2^K - 1 pixels.
        Pass {
            Name "ShadowPropagate"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag_propagate
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_ShadowSrc); SAMPLER(sampler_ShadowSrc);
            float2 _ShadowStepDir;  // UV-space offset toward the sun; doubles each pass

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS);
                return OUT;
            }

            float4 frag_propagate(Varyings IN) : SV_Target {
                float2 uv = IN.positionCS.xy / _ScreenParams.xy;
                float s0 = SAMPLE_TEXTURE2D(_ShadowSrc, sampler_ShadowSrc, uv).r;
                float s1 = SAMPLE_TEXTURE2D(_ShadowSrc, sampler_ShadowSrc, uv + _ShadowStepDir).r;
                return float4(max(s0, s1), 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
