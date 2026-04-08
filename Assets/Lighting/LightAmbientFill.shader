// Fullscreen pass: writes spatially-varying ambient light into the light RT.
// Samples _SkyExposureTex (per-tile exposure, 0 underground / 1 sky) and
// outputs _AmbientColor * exposure. Additive blend onto the black-cleared RT.
//
// Used by LightPass in place of a uniform ambient clear, so underground areas
// receive no ambient light while the surface stays fully lit.
Shader "Hidden/LightAmbientFill" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        BlendOp Add
        Blend One One
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "AmbientFill"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _AmbientColor;

            // Camera world bounds: (minX, minY, width, height).
            // Set by LightPass each frame from the camera's ortho size + position.
            float4 _CamWorldBounds;

            // Tile grid size: (nx, ny, 0, 0).
            float4 _GridSize;

            TEXTURE2D(_SkyExposureTex);
            SAMPLER(sampler_SkyExposureTex);

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
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                // Screen UV → world position.
                float2 worldPos = _CamWorldBounds.xy + IN.uv * _CamWorldBounds.zw;

                // World position → tile grid UV (tiles centered at integers, grid at -0.5).
                float2 tileUV = (worldPos + 0.5) / _GridSize.xy;

                float exposure = SAMPLE_TEXTURE2D(_SkyExposureTex, sampler_SkyExposureTex, tileUV).r;
                return float4(_AmbientColor.rgb * exposure, 1.0);
            }
            ENDHLSL
        }
    }
}
