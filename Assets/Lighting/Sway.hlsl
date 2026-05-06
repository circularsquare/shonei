// Shared plant wind-sway shader code — used by Custom/PlantSprite (visible
// pass) and Hidden/NormalsCapture (lighting normals pass) so the two paths
// can't drift. Tweak the formula or tunables here in one place.
//
// Per-renderer MPB (written by LightReceiverUtil.SetPlantSwayMPB on plant
// construction / extension claim / growth):
//   _PlantBaseY  — world Y of the plant's anchor tile. Same for every SR of
//                  one plant. Sway weight ramps up from this Y.
//   _PlantHeight — current tile-height of the plant (1 for grass / juvenile
//                  bamboo; 3 for full-grown bamboo). Normalises sway so the
//                  top tip of any species reaches the same peak amplitude.
//   _PlantPhase  — small per-instance phase offset, derived from world coords.
//   _PlantSway   — gate flag (0 = no sway, 1 = sway). Default 0 keeps every
//                  non-plant sprite untouched in the shared NormalsCapture
//                  override path.
//   _UseMask     — sway-mode flag, only meaningful when _PlantSway = 1.
//                  0 = vertex-mode (whole-quad cantilever bend, current
//                  behaviour for unmasked plants).
//                  1 = mask-mode (vertex stays put; fragment shifts UV by
//                  amplitude × per-pixel mask R channel — used by trees
//                  with rigid trunks).
//
// Globals (set by WindShaderController):
//   _Wind         — current wind scalar, [-1, 1] typical. Positive = right.
//   _SwayLean     — wind-driven steady lean, world units per unit-wind at
//                   weight=1.
//   _SwayGust     — wind-driven oscillation amplitude on top of lean.
//   _SwayAmbient  — still-air idle sway amplitude.
//   _SwayFreq     — base oscillation frequency in rad/sec.

#ifndef SHONEI_SWAY_INCLUDED
#define SHONEI_SWAY_INCLUDED

float _PlantBaseY;
float _PlantHeight;
float _PlantPhase;
float _PlantSway;
// 0 = vertex-mode (height-weighted whole-quad bend), 1 = mask-mode
// (per-pixel UV displacement via _SwayMask). Each shader still declares
// the _SwayMask texture/sampler itself — only the gate flag lives here.
float _UseMask;

float _Wind;
float _SwayLean;
float _SwayGust;
float _SwayAmbient;
float _SwayFreq;

// Raw sway amplitude in world units, with no per-vertex / per-pixel weighting.
// This is the full peak-to-peak excursion that the top of an unmasked plant
// reaches under a given wind, and the value that mask-mode plants multiply
// by their per-pixel mask R channel.
float SwayAmplitude() {
    float phase  = _Time.y * _SwayFreq + _PlantPhase;
    float amb    = sin(phase)         * _SwayAmbient;
    float lean   = _Wind * _SwayLean + sin(phase * 1.7) * _Wind * _SwayGust;
    return amb + lean;
}

// Vertex-mode sway: amplitude scaled by the cantilever weight pow(dy/h, 1.5).
// Bottom of plant returns 0; top peaks at SwayAmplitude regardless of plant
// height (height-normalised). Used by unmasked plants for whole-quad bend.
// Mask-mode plants ignore this and weight per-pixel via _SwayMask instead.
float SwayOffsetForVertex(float worldY) {
    float dy     = max(0.0, worldY - _PlantBaseY);
    float t      = saturate(dy / max(_PlantHeight, 1.0));
    float weight = t * sqrt(t);  // = pow(t, 1.5), cheaper
    return SwayAmplitude() * weight;
}

#endif
