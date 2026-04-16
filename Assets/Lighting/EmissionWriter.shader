// Writes per-sprite emission contributions into the lightmap RT (_CustomLightRT)
// so flame / glow pixels survive the LightComposite multiply. Used as an
// override material by LightPass after point lights & sun, before composite.
//
// Sprites declare emission via the `_EmissionMap` secondary texture (wired
// automatically by SpriteNormalMapGenerator when a `{stem}_f.png` companion
// exists). `_EmissionMap.a` is the mask strength: 0 = inert, 1 = full glow.
//
// Output is white scaled by mask alpha:
//   write   = (a, a, a, 1)
//   blend   = additive (One One)
//   result  = lightmap saturates at flame pixels → composite multiply preserves
//             the painted color in the source sprite verbatim.
//
// Sort masking: samples _CapturedNormalsRT.b (the topmost lit sprite's sort
// bucket at this pixel, written by NormalsCapturePass) and discards if
// something with a higher bucket is in front. Without this, an emission pixel
// would still write white to the lightmap even when occluded by an animal in
// front, leaving the occluder unlit-bright after composite. _SortBucket is the
// emitter's own bucket, set per-renderer by LightReceiverUtil.SetSortBucket.
//
// Why white-by-mask rather than colored emission: writing colored emission and
// then multiplying onto the painted sprite color *darkens* the painted color
// (orange × orange = dim orange). White preserves it. Colored radial glow that
// tints neighbors is the LightSource component's job, not this pass.
//
// Sprites without an `_EmissionMap` get the "black" fallback texture (declared
// as the property default), so this pass is a free no-op for them.
Shader "Hidden/EmissionWriter" {
    Properties {
        _MainTex        ("Sprite Texture", 2D)    = "white" {}
        _EmissionMap    ("Emission",       2D)    = "black" {}
        // Per-renderer MPB. Default 1 means sprites without a LightSource gating
        // their emission (e.g. always-on glowing crystals) emit at full strength.
        _EmissionScale  ("Emission Scale", Float) = 1
    }
    SubShader {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One One   // additive into the lightmap RT

        Pass {
            Tags { "LightMode"="Universal2D" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);            SAMPLER(sampler_MainTex);
            TEXTURE2D(_EmissionMap);        SAMPLER(sampler_EmissionMap);
            TEXTURE2D(_CapturedNormalsRT);  SAMPLER(sampler_CapturedNormalsRT);

            // Per-renderer MPB, written by LightReceiverUtil.SetSortBucket.
            // This emitter's own sort bucket — sprites with higher buckets in
            // _CapturedNormalsRT are "in front" of us and occlude our emission.
            float _SortBucket;

            // Per-renderer MPB, written by LightSource each frame. 0 = source
            // is currently not emitting light (daytime, out of fuel, disabled),
            // so suppress emission. Default 1 (Properties block) for sprites
            // with no LightSource component on the GameObject.
            float _EmissionScale;

            // Global, set per-frame by LightFeature.LightPass — overall tuning
            // dial in the URP Renderer asset inspector.
            float _EmissionStrength;

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
                OUT.positionCS = TransformWorldToHClip(TransformObjectToWorld(IN.positionOS));
                OUT.uv         = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                // Clip transparent sprite pixels — emission only contributes
                // where the sprite is actually drawn.
                float spriteA = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                clip(spriteA - 0.1);

                // Sort-mask: if a sprite with a higher sort bucket has overdrawn
                // us in the normals RT, we're occluded from the camera's view
                // here and shouldn't bleed emission onto the occluder. Same
                // screenUV convention as LightCircle (no Y-flip — DrawRenderers
                // and the projection match).
                float2 screenUV = IN.positionCS.xy / _ScreenParams.xy;
                float topBucket = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, screenUV).b;
                // Strict > with epsilon < 1/255 — tolerates equal buckets, only
                // discards when something is genuinely in front.
                if (topBucket > _SortBucket + 0.001) discard;

                float a = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).a
                          * _EmissionStrength * _EmissionScale;
                return float4(a, a, a, 1);
            }
            ENDHLSL
        }
    }
}
