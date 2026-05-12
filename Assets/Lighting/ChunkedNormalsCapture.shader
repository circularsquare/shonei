// Normals-capture override material for chunked tile meshes (body, overlay,
// snow). Samples Texture2DArray slices using the per-vertex slice indices
// carried in TEXCOORD1.xy:
//   .x → _MainTexArr slice (used only for the alpha clip)
//   .y → _NormalArr  slice (RGB = bevel normal, A = edge-distance falloff)
//
// LightFeature.NormalsCapturePass invokes this as overrideMaterial when
// drawing the TileChunk layer. Tile bodies/overlays/snow are always shadow
// casters — only pass 0 (alpha encoded from edge-depth, range 0.80–1.0)
// is needed. The non-array NormalsCapture.shader still handles every other
// SpriteRenderer in the scene.
//
// Skipped vs. NormalsCapture.shader:
//   - SpriteRenderer.flipX winding-reversal handling (chunked meshes don't flip)
//   - Plant sway (chunked tiles aren't plants)
//   - Lit-only / directional-only passes (tiles are always shadow casters)
Shader "Hidden/ChunkedNormalsCapture" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D_ARRAY(_MainTexArr); SAMPLER(sampler_MainTexArr);
        TEXTURE2D_ARRAY(_NormalArr);  SAMPLER(sampler_NormalArr);

        // Per-renderer MPB, written by TileMeshController on each chunk's MeshRenderer.
        float _SortBucket;

        struct Attributes {
            float3 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
            float2 sliceUV    : TEXCOORD1; // .x = main slice, .y = normal slice
        };

        struct Varyings {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
            float2 slice      : TEXCOORD1;
            // Local +X / +Y in world space — same trick as NormalsCapture.shader,
            // so an arbitrary chunk-parent transform (rotation, uniform scale)
            // still produces correct world-space normals. For our axis-aligned
            // chunks these reduce to (1,0,0) and (0,1,0).
            float3 worldT     : TEXCOORD2;
            float3 worldB     : TEXCOORD3;
        };

        Varyings vert(Attributes IN) {
            Varyings OUT;
            float3 worldPos = TransformObjectToWorld(IN.positionOS);
            OUT.positionCS = TransformWorldToHClip(worldPos);
            OUT.uv         = IN.uv;
            OUT.slice      = IN.sliceUV;
            OUT.worldT     = TransformObjectToWorldDir(float3(1, 0, 0));
            OUT.worldB     = TransformObjectToWorldDir(float3(0, 1, 0));
            return OUT;
        }

        float4 frag(Varyings IN) : SV_Target {
            // Alpha clip from the body/overlay/snow sprite slice — the same
            // pre-baked alpha that defines the tile's silhouette.
            float spriteAlpha = SAMPLE_TEXTURE2D_ARRAY(_MainTexArr, sampler_MainTexArr, IN.uv, IN.slice.x).a;
            clip(spriteAlpha - 0.1);

            // Tangent-space normal from the normal-map slice (always the body
            // type's normal-map array — overlay/snow point .y at the body
            // type's normal slice, see TileMeshController).
            float4 ns = SAMPLE_TEXTURE2D_ARRAY(_NormalArr, sampler_NormalArr, IN.uv, IN.slice.y);
            float3 tn = ns.rgb * 2.0 - 1.0;

            // Tangent → world. Sprite's local +Z (out of texture) → world -Z (toward camera).
            float3 wn = normalize(tn.x * IN.worldT + tn.y * IN.worldB + tn.z * float3(0, 0, -1));

            // Shadow-caster alpha — same encoding as NormalsCapture pass 0.
            // _NormalArr.a carries edge-distance falloff: 1.0 = at exposed edge,
            // 0.0 = deep interior. Result lerps into [0.80, 1.0] so the lighting
            // tier check (alpha > 0.75) still classifies the pixel as a shadow caster.
            float outAlpha = lerp(0.80, 1.0, ns.a);

            float2 packedXY = wn.xy * 0.5 + 0.5;
            return float4(packedXY, _SortBucket, outAlpha);
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
