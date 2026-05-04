// Tiles a wall texture across a world-spanning sprite, masked by _MainTex.
// _MainTex is the low-res mask (nx x ny, PPU=1):
//   R — wall type: 0 = Stone, 255 = Dirt
//   G — top-row flag (uses *Top texture variant)
//   A — opaque where a wall exists, transparent where sky
// Stone/dirt textures tile at world-space UVs (1 repetition per tile).
//
// Two passes with identical bodies: Universal2D for our LightFeature's
// NormalsCapture filter (which keys on that LightMode tag), and UniversalForward
// for the Universal Renderer's transparent queue. Both are needed when this
// shader runs under URP's Universal renderer (the 2D Renderer would only need
// Universal2D, but having both keeps the shader portable across both).
Shader "Custom/BackgroundTile" {
    Properties {
        _MainTex        ("Mask (auto-set by SpriteRenderer)", 2D) = "white" {}
        _WallTex        ("Stone Wall Texture",                 2D) = "gray"  {}
        _WallTopTex     ("Stone Wall Top Texture",             2D) = "gray"  {}
        _DirtWallTex    ("Dirt Wall Texture",                  2D) = "gray"  {}
        _DirtWallTopTex ("Dirt Wall Top Texture",              2D) = "gray"  {}
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);        SAMPLER(sampler_MainTex);
        TEXTURE2D(_WallTex);        SAMPLER(sampler_WallTex);
        TEXTURE2D(_WallTopTex);     SAMPLER(sampler_WallTopTex);
        TEXTURE2D(_DirtWallTex);    SAMPLER(sampler_DirtWallTex);
        TEXTURE2D(_DirtWallTopTex); SAMPLER(sampler_DirtWallTopTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _WallTex_ST;
        CBUFFER_END

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

        float4 SampleWall(Varyings IN) {
            float4 maskSample = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
            if (maskSample.a < 0.5) discard;

            // +0.5 offset so tile edges align to the grid.
            float2 wallUV = (IN.worldPos + 0.5) * _WallTex_ST.xy + _WallTex_ST.zw;

            float4 stone    = SAMPLE_TEXTURE2D(_WallTex,        sampler_WallTex,        wallUV);
            float4 stoneTop = SAMPLE_TEXTURE2D(_WallTopTex,     sampler_WallTopTex,     wallUV);
            float4 dirt     = SAMPLE_TEXTURE2D(_DirtWallTex,    sampler_DirtWallTex,    wallUV);
            float4 dirtTop  = SAMPLE_TEXTURE2D(_DirtWallTopTex, sampler_DirtWallTopTex, wallUV);

            float topT  = step(0.5, maskSample.g);
            float dirtT = step(0.5, maskSample.r);
            float4 stoneFinal = lerp(stone, stoneTop, topT);
            float4 dirtFinal  = lerp(dirt,  dirtTop,  topT);
            return lerp(stoneFinal, dirtFinal, dirtT);
        }
        ENDHLSL

        Pass {
            Tags { "LightMode" = "Universal2D" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN) : SV_Target { return SampleWall(IN); }
            ENDHLSL
        }

        // UniversalForward fallback — same body. Needed so the sprite still
        // renders under URP's Universal renderer (its transparent queue invokes
        // UniversalForward / SRPDefaultUnlit, not Universal2D).
        Pass {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN) : SV_Target { return SampleWall(IN); }
            ENDHLSL
        }
    }
    Fallback "Sprites/Default"
}
