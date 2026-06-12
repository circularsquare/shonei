using UnityEngine;

// Sort bucket utility — see Assets/spec/SPEC-rendering.md §Lighting / sort-aware
// bucketing. Each lit sprite gets a "bucket" derived from its sortingOrder.
//
// The bucket is encoded in Renderer.renderingLayerMask (1 bit per bucket).
// NormalsCapturePass iterates buckets, writes _SortBucket as a global per
// iteration, and filters DrawRenderers by renderingLayerMask. This replaces
// the older per-sprite MaterialPropertyBlock scheme — MPB usage disables
// SRP Batcher on the renderer, which was the root cause of zero batching
// on every lit sprite in the project.
//
// Bucket boundaries are tuned for the sortingOrder table in SPEC-rendering
// (see "Sorting orders"). Sprites whose sortingOrder changes at runtime
// must call SetBucketFor again — only Inventory floor sort does this today.
//
// Bucket layout:
//   0 Background  ≤ -5    Sky, water underlay, background tiles, water overlay
//   1 Tiles       -4..8   Tile bodies, roads, snow, power shafts
//   2 Buildings   9..16   Buildings, grass overlay, flowers, floor items on
//                         building, platforms, clock hand. Widened from
//                         the natural 10..14 range to swallow parent±1
//                         straddles (flywheel-1, PortStub-1, FurnishingVisuals
//                         slots 0..4).
//   3 Mid         17..47  Floor items on platform, stairs, ladders
//   4 Creatures   48..99  Animals + parts + clothing, plants, light-buildings,
//                         falling items, floor items on dirt
//   5 Foreground  100+    Blueprints, blueprint frame overlay, build preview
public static class SortBucketUtil {
    public const int BucketCount = 6;

    // Normalized _SortBucket value for a bucket index: 0/5, 1/5, ..., 5/5.
    // Read by NormalsCapture / EmissionWriter shaders to write into the
    // captured-normals RT's B channel for sort-aware light shaping.
    public static float BucketToNormalized(int bucket){
        return bucket / (float)(BucketCount - 1);
    }

    // Map a sortingOrder to its bucket (0..5).
    public static int GetBucket(int sortingOrder){
        if (sortingOrder <= -5)  return 0; // Background
        if (sortingOrder <= 8)   return 1; // Tiles
        if (sortingOrder <= 16)  return 2; // Buildings
        if (sortingOrder <= 47)  return 3; // Mid
        if (sortingOrder <= 99)  return 4; // Creatures
        return 5;                          // Foreground
    }

    // Write the bucket bit onto a SpriteRenderer's renderingLayerMask.
    // Replaces any existing bits — buckets are mutually exclusive. Call this
    // anywhere code sets sr.sortingOrder on a lit sprite. (Same call cadence
    // as the old LightReceiverUtil.SetSortBucket.)
    public static void SetBucketFor(SpriteRenderer sr){
        if (sr == null) { Debug.LogError("SortBucketUtil.SetBucketFor: null SpriteRenderer"); return; }
        int bucket = GetBucket(sr.sortingOrder);
        sr.renderingLayerMask = 1u << bucket;
    }

    // Buildings bucket index (sortingOrder 9..16 in the layout above). Plants light at this
    // bucket so torches front-light them like a building rather than back-lighting them like
    // a creature — see SetExplicitBucket.
    public const int BuildingsBucket = 2;

    // Force a specific lighting bucket on a renderer, decoupled from its sortingOrder. The
    // bucket only affects sort-aware point lighting (which receivers a light counts as "in
    // front" vs "behind"); the visual draw order stays driven by sortingOrder. Plants keep
    // their tall sortingOrder (60, drawn over mice) but light at BuildingsBucket so a nearby
    // torch front-lights them instead of treating them as a creature to back-light.
    public static void SetExplicitBucket(SpriteRenderer sr, int bucket){
        if (sr == null) { Debug.LogError("SortBucketUtil.SetExplicitBucket: null SpriteRenderer"); return; }
        sr.renderingLayerMask = 1u << bucket;
    }
}
