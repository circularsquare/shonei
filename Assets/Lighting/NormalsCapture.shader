// Renders all sprites into a world-space normals RT owned by LightFeature.
// Each sprite's _NormalMap (tangent-space, RGBA32) is decoded and transformed
// to world-space for a flat 2D sprite: tangent (x,y,z) → world (x, y, -z).
// Output is packed 0–1: float3(worldNormal * 0.5 + 0.5).
// Transparent pixels are discarded so the background stays black (flat fallback).
Shader "Hidden/NormalsCapture" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);   SAMPLER(sampler_MainTex);
            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);

            struct Attributes {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS);
                OUT.uv         = IN.uv; // sprite mesh UVs are already in atlas space
                return OUT;
            }

            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                // Discard transparent pixels — background stays black (flat fallback).
                float alpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                clip(alpha - 0.1);

                // Tangent-space normal, RGBA32 packed 0–1.
                float4 ns = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, IN.uv);
                float3 tn = ns.rgb * 2.0 - 1.0;

                // SpriteRenderer.flipX negates mesh vertex X, reversing winding → back face.
                // Negate tangent X to keep the world-space normal pointing the right way.
                if (!isFrontFace) tn.x = -tn.x;

                // For flat 2D sprites facing the camera:
                // tangent X → world +X, tangent Y → world +Y, tangent Z → world -Z.
                float3 wn = normalize(float3(tn.x, tn.y, -tn.z));
                return float4(wn * 0.5 + 0.5, 1.0);
            }
            ENDHLSL
        }
    }
}
