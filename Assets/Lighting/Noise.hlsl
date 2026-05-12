// Shared 2D noise primitives — currently consumed by Hidden/CloudFieldGen,
// but written for reuse (water foam, heat shimmer, grass shimmer, etc).
// Single source of truth so any tuning here propagates everywhere.
//
// All functions are float-precision and stateless. Coordinates are in
// "noise space" — the caller is responsible for scaling world units →
// noise frequency (e.g. worldPos * 0.15 for slow-changing clouds).
//
// noise(p) returns roughly [0, 1]. The lattice fade is the standard
// Quilez quintic-free smoothstep (3t² - 2t³) which is C¹ continuous —
// good enough for cloud silhouettes; switch to (6t⁵ - 15t⁴ + 10t³) if
// you ever see gradient banding under self-shadowing.
//
// fbm(p, octaves) is a lacunarity=2, gain=0.5 sum of `noise` octaves.
// `octaves` is dynamic (uniform) — kept as a `[loop]` rather than
// `[unroll]` so callers can crank it from C# without recompiling.

#ifndef SHONEI_NOISE_INCLUDED
#define SHONEI_NOISE_INCLUDED

// Cheap pseudo-random 2D hash, returns [0, 1).
// Quilez's "iqint1" variant via float frac; not cryptographically random
// but visually uncorrelated for the small integer cell coords we feed it.
float Hash2D(float2 p) {
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

// 2D value noise — bilinear lerp of cell-corner hashes with a smoothstep
// fade. Cheap and adequate for cloud-scale features.
float ValueNoise(float2 p) {
    float2 i = floor(p);
    float2 f = frac(p);
    float a = Hash2D(i);
    float b = Hash2D(i + float2(1, 0));
    float c = Hash2D(i + float2(0, 1));
    float d = Hash2D(i + float2(1, 1));
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// fbm — multi-octave value noise. lacunarity=2, gain=0.5. `octaves` is
// dynamic so the same compiled shader can range from 1 (cheap, blocky)
// to 4+ (richer, more storm-cloudy) at runtime.
float fbm(float2 p, int octaves) {
    float sum = 0;
    float amp = 0.5;
    float norm = 0;
    [loop] for (int i = 0; i < octaves; i++) {
        sum  += ValueNoise(p) * amp;
        norm += amp;
        p    *= 2.0;
        amp  *= 0.5;
    }
    return sum / max(norm, 1e-5);
}

#endif
