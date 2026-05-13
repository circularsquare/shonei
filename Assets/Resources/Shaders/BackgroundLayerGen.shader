// Bakes the parallax-shifted view of a user-supplied background texture
// into a viewport-shaped RenderTexture, driven by BackgroundLayer.cs via
// Graphics.Blit each LateUpdate. The resulting RT is then bound as
// _MainTex on the SpriteRenderer, so BOTH the visible main pass AND the
// NormalsCapture override material sample the same parallax-adjusted
// image — they see identical alpha masks, no longer disagreeing on
// where the painted mountains are. Without this baking step, NormalsCapture
// would sample the user texture at the sprite's NATIVE (0..1) UVs while
// the visible shader sampled at WORLD-coord UVs with parallax — producing
// the "ghost mountains in the sky at sunset" artifact (lightmap mask
// shaped like un-parallaxed mountains, visible only at times when the
// lightmap and skyLightColor diverge).
//
// Per-pixel: the RT pixel's UV maps to a viewport screen position; that
// reverses into a world position via _CameraPos + (uv − 0.5) ·
// _ViewportSize, which then goes through the same parallax + texture-
// scale math the visible BackgroundLayer.shader used to do inline.
//
// Inputs (set per-frame from BackgroundLayer.LateUpdate):
//   _MainTex         user-supplied tileable texture (via Blit source)
//   _ViewportSize    (worldW, worldH) of the SkyCamera viewport
//   _CameraPos       camera world position
//   _BandCenterY     world-y the texture's vertical centre aligns to
//   _ParallaxOffset  camera × (1 - worldLocking) — same as the previous
//                    inline parallax computation
//   _TexUVScale      1 / texture world size, so multiplying gives UV
Shader "Hidden/BackgroundLayerGen" {
    SubShader {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float2 _ViewportSize;
                float2 _CameraPos;
                float  _BandCenterY;
                float2 _ParallaxOffset;
                float2 _TexUVScale;
            CBUFFER_END

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
                OUT.uv         = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target {
                // RT UV [0,1] → viewport-screen position → world position.
                float2 worldXY = _CameraPos + (IN.uv - 0.5) * _ViewportSize;
                // Subtract parallax offset, divide into texture tiles.
                float2 rel     = worldXY - float2(0, _BandCenterY) - _ParallaxOffset;
                float2 texUV   = rel * _TexUVScale;
                // Vertical clip — texture has its natural altitude; out-
                // of-range pixels are transparent (no ghost tiles above
                // or below the painted band).
                if (texUV.y < 0.0 || texUV.y > 1.0) return half4(0, 0, 0, 0);
                // Horizontal wrap is handled by the texture import's
                // Wrap Mode U setting.
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, texUV);
            }
            ENDHLSL
        }
    }
}
