// Full-screen multiply blit: multiplies the scene by the custom light map.
// final = scene * lightmap  (black RT = black scene, white RT = unchanged scene)
// Used with cmd.Blit, which sets _MainTex automatically.
//
// Sprite pixels (normals RT alpha > 0) get the full lightmap applied.
// Empty sky/background pixels (alpha == 0) blend _SkyLightBlend of the lightmap in,
// so the sun subtly tints the sky (warm sunset glow, etc.). The remaining sky color
// comes from BackgroundCamera.backgroundColor.
Shader "Hidden/LightComposite" {
    Properties {
        _MainTex ("Light Map", 2D) = "white" {}
        _SkyLightBlend ("Sky Light Blend", Range(0, 1)) = 0.4
        _DeepFloor ("Deep Interior Floor", Range(0, 1)) = 0.2
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        Blend DstColor Zero
        BlendOp Add
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "LightComposite"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _SkyLightBlend;
            float _DeepFloor;
            float4 _BaseAmbient;

            // Camera world bounds and grid size for sky exposure lookup.
            float4 _CamWorldBounds;
            float4 _GridSize;
            TEXTURE2D(_SkyExposureTex);
            SAMPLER(sampler_SkyExposureTex);

            // Normals RT alpha > 0 means main camera rendered a sprite at this pixel.
            TEXTURE2D(_CapturedNormalsRT);
            SAMPLER(sampler_CapturedNormalsRT);

            struct Attributes {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float normsAlpha = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, IN.uv).a;

                // No sprite here (empty sky/background) — blend in a fraction of the
                // lightmap so the sun subtly tints the sky (warm sunset, etc.).
                if (normsAlpha < 0.25) {
                    float4 light = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                    return lerp(float4(1, 1, 1, 1), light, _SkyLightBlend);
                }

                float4 light = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);

                // Edge-depth darkening: shadow-caster pixels deep inside tiles
                // get dimmed based on distance from exposed surface.
                // edgeDepth: 1.0 at surface, 0.0 at DEPTH_PX deep.
                // _DeepFloor keeps deep interior from going fully black.
                // Skipped underground (exposure=0) so tiles get flat lighting
                // with no edge-depth variation.
                if (normsAlpha > 0.75) {
                    float2 worldPos = _CamWorldBounds.xy + IN.uv * _CamWorldBounds.zw;
                    float2 tileUV = (worldPos + 0.5) / _GridSize.xy;
                    float exposure = SAMPLE_TEXTURE2D(_SkyExposureTex, sampler_SkyExposureTex, tileUV).r;
                    if (exposure > 0.5) {
                        float edgeDepth = saturate((normsAlpha - 0.80) / 0.20);
                        light.rgb *= lerp(_DeepFloor, 1.0, edgeDepth);
                    }
                }

                // Base ambient floor: never dim below the universal ambient,
                // even deep inside solid tiles underground.
                light.rgb = max(light.rgb, _BaseAmbient.rgb);

                return light;
            }
            ENDHLSL
        }
    }
}
