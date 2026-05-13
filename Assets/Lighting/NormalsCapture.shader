// Renders all sprites into a world-space normals RT owned by LightFeature.
// Each sprite's _NormalMap (tangent-space, RGBA32) is decoded and transformed
// to world space using the sprite's actual transform — so a rotating sprite's
// lit side stays fixed in world space rather than spinning with the texture.
//
// Per-vertex we send worldT (= sprite's local +X in world) and worldB (= local
// +Y in world). The fragment then forms the world normal as
// tn.x*worldT + tn.y*worldB + tn.z*(0,0,-1). For an unrotated, unit-scale
// sprite this reduces to (tn.x, tn.y, -tn.z) — identical to the previous
// behaviour, so static sprites are unaffected.
//
// Output packing (ARGB32):
//   R, G = world-space normal.x, normal.y packed 0–1 (z reconstructed in
//          LightCircle / LightSun via z = -sqrt(1 - x² - y²), assumes
//          camera-facing sprite normals)
//   B    = receiver sort bucket (sortingOrder / 255), read per-pixel in
//          LightCircle to ramp effective light height. Smuggled in via a
//          per-renderer MaterialPropertyBlock (LightReceiverUtil).
//   A    = lighting tier:
//            0.80–1.0 = shadow caster (tiles/buildings) — range encodes
//                       edge depth for light penetration
//            0.5      = lit-only, no shadow (decorations)
//            0.3      = directional-only (clouds, etc.) — sun + ambient only
//            0.0      = no sprite (cleared black — flat fallback)
// Transparent pixels are discarded so the background stays black (flat fallback).
//
// Tiles use pre-baked 20×20 sprites (TileSpriteCache) whose alpha already
// encodes the border shape. Non-tile sprites clip on their own _MainTex alpha.
//
// Pass 0 — shadow casters (alpha = edge depth mapped to 0.80–1.0)
// Pass 1 — lit-only, no shadow (alpha = 0.5)
// Pass 2 — directional-only, no shadow (alpha = 0.3)
Shader "Hidden/NormalsCapture" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Sway.hlsl"

        TEXTURE2D(_MainTex);   SAMPLER(sampler_MainTex);
        TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
        // Per-renderer when bound (auto-bound by Unity from sprite secondary
        // textures named "_SwayMask"); falls back to a global white texture
        // set in WindShaderController for non-plant sprites.
        TEXTURE2D(_SwayMask);  SAMPLER(sampler_SwayMask);

        // Per-renderer MPB, written by LightReceiverUtil.SetSortBucket.
        // Default 0 for sprites that never had SetSortBucket called — they
        // read as "sort 0" (ground level), which is safe for most fallbacks.
        float _SortBucket;

        struct Attributes {
            float3 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
        };

        struct Varyings {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
            // Sprite's local +X / +Y axes expressed in world space. Carries the
            // object's rotation (and uniform scale, harmlessly — we normalize).
            float3 worldT     : TEXCOORD1;
            float3 worldB     : TEXCOORD2;
        };

        Varyings vert(Attributes IN) {
            Varyings OUT;
            // Vertex-mode plants (no sway mask) apply cantilever sway here so
            // captured normals follow the visible bend. Mask-mode plants leave
            // geometry alone — the fragment stage shifts UVs instead. Non-plant
            // sprites pass through unchanged because _PlantSway defaults to 0.
            // worldT/worldB are unaffected: sway shifts vertex position but
            // doesn't rotate the sprite's local axes, so the tangent → world
            // basis used to decode the normal map stays correct.
            float3 worldPos = TransformObjectToWorld(IN.positionOS);
            // Same role-aware shift as PlantSprite.shader. Stem SRs and
            // unmasked plants get per-vertex weighted bend; head SRs get a
            // single _HeadCenterY-based amount so the captured normals follow
            // the visible head's rigid translation. See Sway.hlsl.
            if (_PlantSway > 0.5) {
                worldPos.x += PlantVertexShift(worldPos.y);
            }
            OUT.positionCS = TransformWorldToHClip(worldPos);
            OUT.uv         = IN.uv;
            OUT.worldT     = TransformObjectToWorldDir(float3(1, 0, 0));
            OUT.worldB     = TransformObjectToWorldDir(float3(0, 1, 0));
            return OUT;
        }

        float4 FragWithAlpha(Varyings IN, bool isFrontFace, float shadowAlpha) {
            // Mask-discard mode (flowers with auto head-mask): match PlantSprite.shader's
            // frag — each SR keeps only its own half (stem vs head). Without this the
            // normal-map pass captures pixels the visible pass discarded, so lighting
            // highlights drift outside the sprite silhouette as the head translates.
            if (_UseMask > 0.5) {
                float mask = SAMPLE_TEXTURE2D(_SwayMask, sampler_SwayMask, IN.uv).r;
                if ((mask > 0.5) != (_RoleIsHead > 0.5)) discard;
            }

            float2 sampleUV = IN.uv;

            // Discard transparent pixels — background stays black (flat fallback).
            // For tiles: the pre-baked 20×20 sprite alpha defines the border shape.
            // For non-tiles: standard sprite alpha transparency.
            float spriteAlpha = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, sampleUV).a;
            clip(spriteAlpha - 0.1);

            // Tangent-space normal, RGBA32 packed 0–1.
            float4 ns = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, sampleUV);
            float3 tn = ns.rgb * 2.0 - 1.0;

            // SpriteRenderer.flipX negates mesh vertex X, reversing winding → back face.
            // Negate tangent X to keep the world-space normal pointing the right way.
            // (worldT/worldB come from the GameObject's transform, which flipX doesn't
            // touch — flipX is a mesh-vertex flip — so this still composes correctly.)
            if (!isFrontFace) tn.x = -tn.x;

            // Tangent → world: rotate (and reflect) the sampled normal by the sprite's
            // own basis. Sprite's local +Z (out of the texture) → world -Z (toward camera).
            float3 wn = normalize(tn.x * IN.worldT + tn.y * IN.worldB + tn.z * float3(0, 0, -1));

            // For shadow casters (pass 0), encode edge depth in alpha.
            // _NormalMap.a carries edge-distance falloff baked by TileSpriteCache:
            //   1.0 = at exposed edge (fully lit), 0.0 = deep interior (dark).
            // Non-tile sprites have _NormalMap.a = 1.0 → lerp(0.80, 1.0, 1.0) = 1.0.
            float outAlpha = shadowAlpha;
            if (shadowAlpha > 0.75)
                outAlpha = lerp(0.80, 1.0, ns.a);

            // R, G = normal.xy packed. B = sort bucket (not normal.z — the
            // light shaders reconstruct z from xy to free this channel).
            float2 packedXY = wn.xy * 0.5 + 0.5;
            return float4(packedXY, _SortBucket, outAlpha);
        }
        ENDHLSL

        // Pass 0: shadow casters — alpha = edge depth mapped to 0.80–1.0
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                return FragWithAlpha(IN, isFrontFace, 1.0);
            }
            ENDHLSL
        }

        // Pass 1: lit-only, no shadow — alpha = 0.5
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                return FragWithAlpha(IN, isFrontFace, 0.5);
            }
            ENDHLSL
        }

        // Pass 2: directional-only, no shadow — alpha = 0.3
        // Sprites on this pass receive sun + ambient light only.
        // LightCircle.shader skips pixels with alpha < 0.4, so torch/point lights
        // never reach these sprites even though LightComposite applies the lightmap.
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target {
                return FragWithAlpha(IN, isFrontFace, 0.3);
            }
            ENDHLSL
        }
    }
}
