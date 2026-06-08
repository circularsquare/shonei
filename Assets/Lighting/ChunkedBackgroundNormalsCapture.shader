// Normals-capture override for chunked BACKGROUND wall meshes. Flat-lit variant
// of Hidden/ChunkedNormalsCapture: v1 background walls carry no normal-map array
// (no relief), so this writes a flat camera-facing normal and the lit-only alpha
// tier (0.5) — matching what the retired full-screen background sprite captured
// via NormalsCaptureBackground. Only the body slice (TEXCOORD1.x) is read, for the
// alpha clip that defines the wall silhouette and its soft edges (against sky and
// against the other wall type).
//
// LightFeature.NormalsCapturePass invokes this as overrideMaterial when drawing the
// BackgroundTileChunk layer, before the main bucket loops so every other tier
// overwrites it (background is the backmost tier).
Shader "Hidden/ChunkedBackgroundNormalsCapture" {
    SubShader {
        Tags { "RenderType" = "Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D_ARRAY(_MainTexArr); SAMPLER(sampler_MainTexArr);

        // Per-chunk MPB, written by BackgroundTileMeshController on each chunk's MeshRenderer.
        float _SortBucket;

        struct Attributes {
            float3 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
            float2 sliceUV    : TEXCOORD1; // .x = body slice (.y unused — no normal array)
        };

        struct Varyings {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
            float  slice      : TEXCOORD1;
        };

        Varyings vert(Attributes IN) {
            Varyings OUT;
            OUT.positionCS = TransformWorldToHClip(TransformObjectToWorld(IN.positionOS));
            OUT.uv         = IN.uv;
            OUT.slice      = IN.sliceUV.x;
            return OUT;
        }

        float4 frag(Varyings IN) : SV_Target {
            // Alpha clip from the body slice — the pre-baked alpha that defines the
            // wall silhouette and its soft edges against sky / the other wall type.
            float spriteAlpha = SAMPLE_TEXTURE2D_ARRAY(_MainTexArr, sampler_MainTexArr, IN.uv, IN.slice).a;
            clip(spriteAlpha - 0.1);

            // Flat camera-facing normal (no relief in v1): packed xy = 0.5 → decoded
            // (0,0) → reconstructed (0,0,-1). Alpha 0.5 = lit-only tier (full light,
            // no underground edge-depth darkening) — same as the old background sprite.
            return float4(0.5, 0.5, _SortBucket, 0.5);
        }
        ENDHLSL

        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
