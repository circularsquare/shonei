using System;

// Centralised RNG. All gameplay-affecting randomness (recipe selection, animal AI,
// weather, mouse names, etc.) goes through Rng instead of UnityEngine.Random or
// raw `new System.Random()` so that a save's future is reproducible from its seed.
//
// Cosmetic-only randomness (particles, animation jitter) can keep using
// UnityEngine.Random — Rng is for anything that affects world state across ticks.
//
// Initialised exactly once per world by WorldController.GenerateDefault (new world)
// or by SaveSystem.Load (saved world). The `worldSeed` is persisted in WorldSaveData
// so reload reproduces the original stream.
//
// API mirrors UnityEngine.Random shapes so callsite substitution is mechanical:
//   Random.Range(int, int)   → Rng.Range(int, int)         // [min, max)
//   Random.Range(float, float) → Rng.Range(float, float)   // [min, max)
//   Random.value             → Rng.value                   // [0, 1)
//   new System.Random()      → Rng.SubRng()                // independent stream
public static class Rng {
    static System.Random rng = new System.Random();
    public static int worldSeed { get; private set; }

    // Reseed the global stream. Call once per world from WorldController/SaveSystem.
    public static void Init(int seed) {
        worldSeed = seed;
        rng = new System.Random(seed);
    }

    // [minInclusive, maxExclusive) — matches UnityEngine.Random.Range(int, int).
    public static int Range(int minInclusive, int maxExclusive) => rng.Next(minInclusive, maxExclusive);

    // [min, max) in practice — UnityEngine docs say inclusive upper but floats make
    // the distinction immaterial. Callers that needed exact-inclusive max are doing
    // something they shouldn't.
    public static float Range(float min, float max) => min + (float)rng.NextDouble() * (max - min);

    // [0, 1) — matches UnityEngine.Random.value.
    public static float value => (float)rng.NextDouble();

    public static double NextDouble() => rng.NextDouble();

    // Any non-negative int. Useful for deriving stable per-entity seeds at creation time.
    public static int NextInt() => rng.Next();

    // Returns an independent System.Random seeded from this stream. Used for entities
    // (e.g. each Animal) that want their own stream but still want overall reproducibility.
    // The returned RNG is independent — consuming it doesn't advance the global stream.
    public static System.Random SubRng() => new System.Random(rng.Next());

    // Editor/test convenience: assert that a seed has been set. Returns false in
    // pre-init contexts (e.g. early Awake) so callers can branch if needed.
    public static bool IsInitialized => worldSeed != 0;
}
