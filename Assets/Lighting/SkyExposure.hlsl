// Shared sky exposure lookup — used by LightAmbientFill and LightSun.
// Globals set by: _CamWorldBounds (LightPass per-camera),
//                 _GridSize + _SkyExposureTex (SkyExposure.cs on dirty).

#ifndef SKY_EXPOSURE_INCLUDED
#define SKY_EXPOSURE_INCLUDED

float4 _CamWorldBounds; // (minX, minY, width, height) of the current camera
float4 _GridSize;       // (nx, ny, 0, 0) tile grid dimensions

TEXTURE2D(_SkyExposureTex);
SAMPLER(sampler_SkyExposureTex);

// screenUV: 0–1 across the current camera's viewport (typically IN.uv from a fullscreen blit).
// Returns 0 (deep underground) to 1 (sky-exposed surface).
float SampleSkyExposure(float2 screenUV) {
    float2 worldPos = _CamWorldBounds.xy + screenUV * _CamWorldBounds.zw;
    float2 tileUV   = (worldPos + 0.5) / _GridSize.xy;
    return SAMPLE_TEXTURE2D(_SkyExposureTex, sampler_SkyExposureTex, tileUV).r;
}

#endif
