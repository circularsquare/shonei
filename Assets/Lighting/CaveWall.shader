// Tiles a wall texture across a world-spanning sprite, masked by _MainTex.
// _MainTex is the low-res mask (nx x ny, PPU=1): opaque where wall exists,
// transparent where sky. _WallTex is the tileable wall texture, sampled at
// world-space UVs so each tile gets one full repetition of the texture.
Shader "Custom/CaveWall" {
    Properties {
        _MainTex ("Mask (auto-set by SpriteRenderer)", 2D) = "white" {}
        _WallTex ("Tileable Wall Texture", 2D) = "gray" {}
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_WallTex);
            SAMPLER(sampler_WallTex);
            float4 _WallTex_ST;

            struct Attributes {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float2 worldPos   : TEXCOORD1;
            };

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS);
                OUT.uv = IN.uv;
                // World position for tiling. Each tile is 1 world unit, so
                // world pos directly gives tile-aligned UVs.
                float3 wp = TransformObjectToWorld(IN.positionOS);
                OUT.worldPos = wp.xy;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                float mask = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                if (mask < 0.5) discard;

                // Tile the wall texture at 1 repetition per 4 world units.
                // +0.5 offset so tile edges align to the grid.
                float2 wallUV = (IN.worldPos + 0.5) / 4.0 * _WallTex_ST.xy + _WallTex_ST.zw;
                float4 wall = SAMPLE_TEXTURE2D(_WallTex, sampler_WallTex, wallUV);
                return wall;
            }
            ENDHLSL
        }
    }
}
