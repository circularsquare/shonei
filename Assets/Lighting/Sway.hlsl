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
//   _SwayAmount  — per-renderer 0..1 scalar that linearly attenuates the
//                  computed amplitude. 1 = full sway (default for plants),
//                  0 = no sway (rigid decorations like mushrooms). Lives
//                  alongside _PlantSway: when callers want zero motion they
//                  set _PlantSway=0 to skip the math entirely; intermediate
//                  values (0 < x < 1) keep _PlantSway=1 and let this scalar
//                  do the attenuation so two same-species sprites can have
//                  different stiffness without forking the shader.
//   _RoleIsHead  — only meaningful when _UseMask = 1 (mask-discard mode).
//                  0 = stem half (this SR keeps mask < 0.5 pixels, applies
//                      the usual Y-weighted vertex bend on top of them).
//                  1 = head half (this SR keeps mask > 0.5 pixels, all
//                      vertices uniformly shift by SwayOffsetForVertex at
//                      _HeadCenterY — so the head reads as one rigid chunk
//                      whose anchor point bends with the stem-top below it).
//                  See FlowerController for the two-SR composition (one
//                  stem SR + one head SR per flower with a `_sway` mask).
//   _HeadCenterY — head's natural anchor height above _PlantBaseY, in
//                  WORLD UNITS (not normalised). Computed once per
//                  FlowerType from the average Y of white pixels in the
//                  mask. Drives a single SwayOffsetForVertex call so all
//                  head vertices shift by the same amount and the head
//                  doesn't visually distort.
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
// Per-renderer linear attenuation, [0..1]. SetPlantSwayMPB writes 1 by
// default so existing callers keep full sway. Non-plant SRs that never
// hit SetPlantSwayMPB read 0 here, but _PlantSway is also 0 for them
// so the gate skips the math regardless.
float _SwayAmount;

// Head-role / head-anchor for masked flower decorations. Default 0
// (stem / no-role) so existing plants keep their per-vertex weighted
// bend. See header comment block above.
float _RoleIsHead;
float _HeadCenterY;

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
    return (amb + lean) * _SwayAmount;
}

// Vertex-mode sway: amplitude scaled by the cantilever weight pow(dy/h, 1.5).
// Bottom of plant returns 0; top peaks at SwayAmplitude regardless of plant
// height (height-normalised). Used by unmasked plants and the stem half of
// masked flowers (per-vertex). Head halves call HeadShiftOffset instead so
// every vertex gets the same shift.
float SwayOffsetForVertex(float worldY) {
    float dy     = max(0.0, worldY - _PlantBaseY);
    float t      = saturate(dy / max(_PlantHeight, 1.0));
    float weight = t * sqrt(t);  // = pow(t, 1.5), cheaper
    return SwayAmplitude() * weight;
}

// Head-mode sway: every vertex of the head SR shifts by the same amount,
// computed at the head's centroid Y. Uses a LINEAR weight rather than the
// pow(t, 1.5) used by SwayOffsetForVertex — and that's deliberate.
//
// Why linear: the stem SR is a 4-vertex quad. Its vertex shader computes
// pow(t, 1.5) at the corners (t=0 → 0, t=1 → 1) and Unity linearly
// interpolates between corners for every fragment in between. So the
// stem's *visible* bend is linear in Y, even though the formula is
// non-linear. If the head uses true cantilever pow(t, 1.5) here, it ends
// up shifting less than the stem-top visibly does at the join — the head
// reads as floating BEHIND the stem-top instead of sitting on it. Linear
// keeps them consistent.
//
// If we ever subdivide the stem mesh (or move stem displacement to the
// fragment stage) to get true cantilever, switch this back to
// SwayOffsetForVertex(_PlantBaseY + _HeadCenterY).
float HeadShiftOffset() {
    float t = saturate(_HeadCenterY / max(_PlantHeight, 1.0));
    return SwayAmplitude() * t;
}

// Picks the right per-vertex shift for the current SR's role. Stem and
// regular plants weight by the vertex's own Y; head SRs use a single
// _HeadCenterY-based amount. Centralised so PlantSprite.shader and
// NormalsCapture.shader can't drift from each other.
float PlantVertexShift(float worldY) {
    return _RoleIsHead > 0.5 ? HeadShiftOffset() : SwayOffsetForVertex(worldY);
}

#endif
