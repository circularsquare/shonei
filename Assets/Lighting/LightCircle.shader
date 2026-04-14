// Draws a soft radial gradient for one point light source, modulated by NdotL
// from _CapturedNormalsRT (world-space normals rendered by NormalsCapturePass).
// Max blend: overlapping sources take the brightest value without blowing out,
// and torches compete with (rather than stack on) ambient light.
Shader "Hidden/LightCircle" {
    SubShader {
        Tags { "RenderType"="Opaque" }
        BlendOp Max
        Blend One One
        ZWrite Off
        ZTest Always
        Cull Off

        Pass {
            Name "LightCircle"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Set per-draw via MaterialPropertyBlock.
            float4 _LightColor;
            float  _Intensity;
            float  _InnerFraction;     // innerRadius / outerRadius  (0–1)
            float4 _LightWorldPos;     // world-space XY of the light source
            float  _LightHeight;       // Z offset above sprite plane for NdotL angle
            float  _LightSortBucket;   // this light's sortingOrder / 255 (see LightSource.sortBucket)
            float  _AmbientNormal;     // minimum NdotL floor (softens back-face darkness)

            // Global ramp params (set once per frame by LightPass).
            // _SortRampRange         = sort-delta range (in normalized bucket units) over
            //                         which the effective height ramps, on the behind side,
            //                         from +_LightHeight toward +_LightHeight*_BehindFarHeightFactor.
            // _BehindFarHeightFactor = height scale at the far end of the behind ramp.
            //                         1.0 = flat ramp (uniform behind lighting);
            //                         >1 = steeper/more top-down for deep-behind receivers;
            //                         <1 = shallower/more grazing.
            float  _SortRampRange;
            float  _BehindFarHeightFactor;

            // Populated each frame by NormalsCapturePass.
            TEXTURE2D(_CapturedNormalsRT);
            SAMPLER(sampler_CapturedNormalsRT);

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
                float3 worldPos3 = TransformObjectToWorld(IN.positionOS);
                OUT.positionCS   = TransformWorldToHClip(worldPos3);
                OUT.uv           = IN.uv;
                OUT.worldPos     = worldPos3.xy;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target {
                // Radial falloff.
                float2 d       = IN.uv - 0.5;
                float  r       = length(d);
                float  inner   = _InnerFraction * 0.5;
                float  falloff = 1.0 - smoothstep(inner, 0.5, r);
                falloff = falloff * falloff;

                // Sample world-space normals RT at this fragment's screen position.
                // No Y-flip: DrawRenderers writes _CapturedNormalsRT in OpenGL convention
                // (V=0 at bottom). positionCS.y is also 0 at the bottom (renderIntoTexture:false
                // projection used by LightPass), so they match directly. Flipping here
                // displaces normals vertically and inverts the Y lighting direction.
                float2 screenUV = IN.positionCS.xy / _ScreenParams.xy;
                float4 ns = SAMPLE_TEXTURE2D(_CapturedNormalsRT, sampler_CapturedNormalsRT, screenUV);

                // Directional-only tier (alpha ≈ 0.3): skip torch contribution entirely.
                // LightComposite still applies the lightmap to these pixels, but since we
                // write nothing here the lightmap only carries ambient + sun for them.
                if (ns.a > 0.0 && ns.a < 0.4) return float4(0, 0, 0, 1);

                // Alpha = 0 means no sprite: use flat camera-facing fallback.
                // (We test alpha, not rgb, because B now carries the sort bucket
                // and can be non-zero even with a flat forward normal.)
                float2 nxy;
                if (ns.a < 0.01) nxy = float2(0, 0);
                else             nxy = ns.rg * 2.0 - 1.0;

                // Reconstruct normal.z from xy. All sprite normal maps in this
                // project have z ≤ 0 (camera-facing), so we take the negative root.
                float nz = -sqrt(saturate(1.0 - dot(nxy, nxy)));
                float3 normal = float3(nxy, nz);

                // Sort-aware lighting:
                //   In front (sortDelta > 0): flip effective height so the light appears
                //     to come from behind the receiver. Forward-facing interior normals
                //     then have negative NdotL (clamped to 0), while edge normals pointing
                //     sideways toward the light's XY still catch it. Ambient floor = 0
                //     prevents the radial falloff from bleeding warm light through the
                //     silhouette.
                //   Behind (sortDelta <= 0): effective height ramps from _LightHeight
                //     (at delta=0) toward _LightHeight * _BehindFarHeightFactor as the
                //     receiver sorts further behind. Default _BehindFarHeightFactor = 1.0
                //     makes this the identity — matches pre-change lighting. Ambient
                //     floor stays as _AmbientNormal.
                float sortDelta = ns.b - _LightSortBucket;
                float effectiveHeight;
                if (sortDelta > 0.0) {
                    effectiveHeight = -_LightHeight;
                } else {
                    float behindT = smoothstep(0.0, max(_SortRampRange, 1e-5), -sortDelta);
                    effectiveHeight = lerp(_LightHeight, _LightHeight * _BehindFarHeightFactor, behindT);
                }
                // EXPERIMENT: ambient floor applied on both sides — radial falloff bleeds
                // through silhouettes again. Set to 0.0 in the in-front branch to restore
                // hard-block behaviour.
                float ambientFloor = _AmbientNormal;

                float3 toLight = normalize(float3(_LightWorldPos.xy - IN.worldPos.xy, -effectiveHeight));
                float  ndotl   = max(ambientFloor, dot(normal, toLight));

                return float4(saturate(_LightColor.rgb * (_Intensity * falloff * ndotl)), 1.0);
            }
            ENDHLSL
        }
    }
}
