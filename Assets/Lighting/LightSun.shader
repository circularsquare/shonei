// Full-screen directional light pass for the sun.
// Samples _CapturedNormalsRT and computes NdotL against the sun direction.
// Modulated by _SkyExposureTex so the sun fades out underground.
// Additive blend: sun highlights add on top of the final point-light RT.
Shader "Hidden/LightSun" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        BlendOp Add
        Blend One One
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "LightSun"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SkyExposure.hlsl"

            float4 _SunColor;
            float  _SunIntensity;
            float3 _SunDir;    // XY world direction toward sun (Z = 0)
            float  _SunHeight; // controls how much sun hits flat/camera-facing surfaces
            float  _AmbientNormal;
            // 1 = ignore the world tile-grid exposure (SkyCamera path);
            // 0 = sample it as normal (Main camera). LightPass sets this per
            // camera. Without it the SkyCamera samples _SkyExposureTex with
            // its own (dampened-zoom) _CamWorldBounds, landing the dark
            // terrain pattern at a screen position that does NOT match where
            // the Main camera draws the terrain — producing a ghost terrain
            // silhouette on the sky, most visible at max zoom-in.
            float  _SkyExposureBypass;
            // Set per frame by LightPass from SettingsManager.floodFill. Under flood-fill, burrow
            // darkening is handled by the SkyExposure pass (sky routed through the door), so the
            // straight-line burrow sun march below is redundant and only adds a hard shadow edge.
            float  _FloodFill;

            // Populated each frame by NormalsCapturePass.
            TEXTURE2D(_CapturedNormalsRT);
            SAMPLER(sampler_CapturedNormalsRT);

            // Burrow walls + interior mask (WallField.cs): B = burrow interior cell; R/G =
            // vertical/horizontal burrow wall edges. _WallTexSize = (nx+1, ny+1).
            TEXTURE2D(_WallBurrowTex);
            SAMPLER(sampler_WallBurrowTex);
            float4 _WallTexSize;

            // Sun occlusion by a burrow's solid shell. A burrow is a hollow carved inside solid tiles,
            // so the sun shouldn't reach its interior except through the door. Only burrow-interior
            // fragments (B) march (gated, so this fullscreen pass costs nothing elsewhere): step toward
            // the sun, accumulate the in-burrow chord, and when the ray crosses a burrow wall commit it
            // as shell thickness → soft shadow (grazing rays → softer). Leaving the burrow without a
            // wall crossing means the ray went out the door → sunlit. Disabled on the SkyCamera.
            float BurrowSunShadow(float2 worldPos) {
                if (_FloodFill > 0.5) return 0.0;       // flood-fill: SkyExposure seals burrows instead (no hard edge)
                if (_SkyExposureBypass > 0.5) return 0.0;
                float2 g    = worldPos + 0.5;
                int2   cell = (int2)floor(g);
                if (SAMPLE_TEXTURE2D(_WallBurrowTex, sampler_WallBurrowTex, ((float2)cell + 0.5) / _WallTexSize.xy).b < 0.5)
                    return 0.0;                                  // not in a burrow → sun unoccluded
                float2 rd     = normalize(_SunDir.xy);
                float2 rdSafe = float2(abs(rd.x) < 1e-6 ? 1e-6 : rd.x, abs(rd.y) < 1e-6 ? 1e-6 : rd.y);
                int2   stp    = int2(rd.x >= 0.0 ? 1 : -1, rd.y >= 0.0 ? 1 : -1);
                float2 tDelta = abs(1.0 / rdSafe);
                float2 nb     = floor(g) + float2(rd.x >= 0.0 ? 1.0 : 0.0, rd.y >= 0.0 ? 1.0 : 0.0);
                float2 tMax   = (nb - g) / rdSafe;
                const float WallFade    = 0.5;   // match LightCircle
                const float BurrowThick = 1.0;   // burrow-edge softness; lower = softer (match LightCircle)
                float pending = 0.0, prevT = 0.0;
                [loop]
                for (int i = 0; i < 20; i++) {
                    float nextT = min(tMax.x, tMax.y);
                    pending += nextT - prevT;
                    prevT = nextT;
                    if (tMax.x < tMax.y) {
                        int lineX = (stp.x > 0) ? cell.x + 1 : cell.x;
                        bool wall = SAMPLE_TEXTURE2D(_WallBurrowTex, sampler_WallBurrowTex, (float2((float)lineX, (float)cell.y) + 0.5) / _WallTexSize.xy).r > 0.5;
                        tMax.x += tDelta.x; cell.x += stp.x;
                        if (wall) return saturate(pending * BurrowThick / WallFade);
                    } else {
                        int lineY = (stp.y > 0) ? cell.y + 1 : cell.y;
                        bool wall = SAMPLE_TEXTURE2D(_WallBurrowTex, sampler_WallBurrowTex, (float2((float)cell.x, (float)lineY) + 0.5) / _WallTexSize.xy).g > 0.5;
                        tMax.y += tDelta.y; cell.y += stp.y;
                        if (wall) return saturate(pending * BurrowThick / WallFade);
                    }
                    // Left the burrow without crossing a wall → out the door/opening → sunlit.
                    if (SAMPLE_TEXTURE2D(_WallBurrowTex, sampler_WallBurrowTex, ((float2)cell + 0.5) / _WallTexSize.xy).b < 0.5)
                        return 0.0;
                }
                return 0.0;
            }

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
                // No Y-flip: see LightCircle.shader for explanation.
                float2 screenUV = IN.positionCS.xy / _ScreenParams.xy;
                float4 ns = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, screenUV);

                // Alpha tier: 1.0 = solid tile, 0.5 = lit-only, 0.0 = no sprite.
                // (isCaster was used by shadow ray march — commented out below.)
                float hasSprite = ns.a > 0.25 ? 1.0 : 0.0;

                // No sprite: use flat camera-facing normal (nxy = 0, nz = -1).
                // B carries the receiver sort bucket (not normal.z); we reconstruct
                // z from xy. All sprite normal maps here have z ≤ 0 (camera-facing).
                float2 nxy = hasSprite > 0.5 ? (ns.rg * 2.0 - 1.0) : float2(0, 0);
                float  nz  = -sqrt(saturate(1.0 - dot(nxy, nxy)));
                float3 normal  = float3(nxy, nz);
                float3 sunDir3 = normalize(float3(_SunDir.xy, -_SunHeight));
                float  ndotl   = max(_AmbientNormal, dot(normal, sunDir3));

                // Shadow ray march disabled for performance. Uncomment to re-enable:
                // float inShadow = 0.0;
                // [branch] if (_ShadowLength > 0.0) {
                //     float2 shadowDir  = normalize(_SunDir.xy);
                //     float2 shadowStep = shadowDir * _ShadowLength * _WorldToUV * (1.0 / 16.0);
                //     for (int i = 1; i <= 16; i++) {
                //         float2 sampleUV = screenUV + shadowStep * i;
                //         float4 caster = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, sampleUV);
                //         if (caster.a > 0.75) { inShadow = 1.0; break; }
                //     }
                // }
                // float sunFactor = 1.0 - inShadow * (1.0 - isCaster) * _ShadowDarkness;

                // Fade sun with distance from sky — same exposure lookup as LightAmbientFill.
                // Bypassed on the SkyCamera (see _SkyExposureBypass declaration above).
                float exposure = lerp(SampleSkyExposure(IN.uv), 1.0, _SkyExposureBypass);

                // Seal the sun out of burrow interiors (except through the door). Ambient is a separate
                // pass, so it still fills the burrow.
                float2 worldPos = _CamWorldBounds.xy + IN.uv * _CamWorldBounds.zw;
                float  sunShadow = BurrowSunShadow(worldPos);

                return float4(saturate(_SunColor.rgb * (_SunIntensity * ndotl * exposure * (1.0 - sunShadow))), 1.0);
            }
            ENDHLSL
        }
    }
}
