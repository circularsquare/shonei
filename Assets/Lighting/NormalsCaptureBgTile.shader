// Renders the background sprite into the normals RT.
// Unlike NormalsCapture.shader, this samples the actual wall textures (set as
// globals by BackgroundTile.cs) and clips pixels where the texture is
// transparent — so the jagged top edge of *walltop correctly reads as sky
// (black/no-sprite) in the normals RT rather than opaque background.
// Outputs flat forward normals since the background is a flat plane.
//
// Pass 0 — shadow casters (alpha = 1.0)  — unused; included for completeness
// Pass 1 — lit-only, no shadow (alpha = 0.5) — used: background is lit but casts no shadow
// Pass 2 — directional-only (alpha = 0.3)    — unused; included for completeness
Shader "Hidden/NormalsCaptureBackground" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // _MainTex = low-res mask (nx x ny, 1 px per tile) from SpriteRenderer.
        //   R: 0 = Stone, 255 = Dirt
        //   G: 255 = top-row (use *Top texture)
        //   A: opaque where a wall exists, transparent where sky
        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        // Set globally by BackgroundTile.cs.
        TEXTURE2D(_BackgroundTex);        SAMPLER(sampler_BackgroundTex);
        TEXTURE2D(_BackgroundTopTex);     SAMPLER(sampler_BackgroundTopTex);
        TEXTURE2D(_BackgroundDirtTex);    SAMPLER(sampler_BackgroundDirtTex);
        TEXTURE2D(_BackgroundDirtTopTex); SAMPLER(sampler_BackgroundDirtTopTex);

        // Per-renderer MPB, written by LightReceiverUtil.SetSortBucket.
        float _SortBucket;

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
            float3 wp      = TransformObjectToWorld(IN.positionOS);
            OUT.positionCS = TransformWorldToHClip(wp);
            OUT.uv         = IN.uv;
            OUT.worldPos   = wp.xy;
            return OUT;
        }

        float4 FragBackground(Varyings IN, float shadowAlpha) {
            // Discard pixels outside the background mask.
            float4 mask = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
            clip(mask.a - 0.5);

            // Tile the wall texture at 1 rep per world unit, same as BackgroundTile.shader.
            float2 wallUV = IN.worldPos + 0.5;

            // Branch on wall type (R) then on top-row (G).
            float4 stone    = SAMPLE_TEXTURE2D(_BackgroundTex,        sampler_BackgroundTex,        wallUV);
            float4 stoneTop = SAMPLE_TEXTURE2D(_BackgroundTopTex,     sampler_BackgroundTopTex,     wallUV);
            float4 dirt     = SAMPLE_TEXTURE2D(_BackgroundDirtTex,    sampler_BackgroundDirtTex,    wallUV);
            float4 dirtTop  = SAMPLE_TEXTURE2D(_BackgroundDirtTopTex, sampler_BackgroundDirtTopTex, wallUV);

            float topT  = step(0.5, mask.g);
            float dirtT = step(0.5, mask.r);
            float4 stoneFinal = lerp(stone, stoneTop, topT);
            float4 dirtFinal  = lerp(dirt,  dirtTop,  topT);
            float4 color      = lerp(stoneFinal, dirtFinal, dirtT);

            // Clip transparent pixels in the selected wall texture — so e.g.
            // the *walltop sky pixels read as sky in the normals RT instead of
            // opaque background.
            clip(color.a - 0.5);

            // Flat camera-facing normal: world (0, 0, -1) → packed xy = (0.5, 0.5).
            // B carries the sort bucket (not normal.z); shaders reconstruct z.
            return float4(0.5, 0.5, _SortBucket, shadowAlpha);
        }
        ENDHLSL

        // Pass 0: shadow casters — alpha = 1.0
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN) : SV_Target { return FragBackground(IN, 1.0); }
            ENDHLSL
        }

        // Pass 1: lit-only, no shadow — alpha = 0.5
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN) : SV_Target { return FragBackground(IN, 0.5); }
            ENDHLSL
        }

        // Pass 2: directional-only, no shadow — alpha = 0.3
        Pass {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(Varyings IN) : SV_Target { return FragBackground(IN, 0.3); }
            ENDHLSL
        }
    }
}
