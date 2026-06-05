using System;
using System.Collections.Generic;
using UnityEngine;

// Persistent sway metadata baked by PlantBlobBaker alongside the per-blob
// sprites. One JSON file per plant lives at
//   Assets/Resources/Sprites/Plants/Split/{plantName}/sway_meta.json
// describing the phase (radians) and a static flag for every blob in every
// growth-stage cell. Plant.cs reads this through PlantSwayMetaCache when
// spawning a tile's child blob SRs.
//
// Schema is shaped for Unity's JsonUtility — flat fields, public arrays.
// Top-level types are non-nested so they serialise without ceremony.

[Serializable]
public class PlantSwayBlobMeta {
    public float phase;     // radians, deterministic hash of the blob's mask colour
    public bool  isStatic;  // pure black or white in the mask → never sways
}

[Serializable]
public class PlantSwayCellMeta {
    public string              cellName;  // "g0", "b4", etc. — matches the file prefix
    public PlantSwayBlobMeta[] blobs;     // one entry per `{cellName}_b{i}.png`
}

[Serializable]
public class PlantSwayMeta {
    public PlantSwayCellMeta[] cells;
}

// Loads + caches per-plant metadata. Plants are spawned in bursts on
// worldgen/load and need the same metadata multiple times — cache avoids
// re-parsing the JSON. Stores null for plants without a sway_meta file
// so the absence-of-bake check is a single dictionary lookup.
public static class PlantSwayMetaCache {
    private static readonly Dictionary<string, PlantSwayMeta> cache
        = new Dictionary<string, PlantSwayMeta>();

    public static PlantSwayMeta Get(string plantName) {
        if (cache.TryGetValue(plantName, out var m)) return m;
        var asset = Resources.Load<TextAsset>("Sprites/Plants/Split/" + plantName + "/sway_meta");
        m = (asset == null) ? null : JsonUtility.FromJson<PlantSwayMeta>(asset.text);
        cache[plantName] = m;
        return m;
    }

    public static PlantSwayCellMeta GetCell(string plantName, string cellName) {
        var m = Get(plantName);
        if (m == null || m.cells == null) return null;
        for (int i = 0; i < m.cells.Length; i++)
            if (m.cells[i].cellName == cellName) return m.cells[i];
        return null;
    }

    // Reload-domain-off play mode (and any future scene reload) leaves this
    // cache populated. The cached values themselves carry no Unity references
    // — purely floats / strings — so they don't break across reloads, but
    // resetting keeps the project's "plain-C# singleton reset" convention
    // (see project_plain_csharp_singletons.md).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCache() {
        cache.Clear();
    }
}

// Loads + caches the packed blob sheet baked by PlantBlobBaker:
//   Assets/Resources/Sprites/Plants/Split/{plantName}/{plantName}_blobs_baked.png
// a Multiple-mode sprite sheet whose sub-sprites are named per cell+layer
// ("g0_static", "g0_b0", "b4_b2", …). Plant.cs looks them up by name when
// building a tile's static SR + child blob SRs. One Resources.LoadAll per plant
// (cached) replaces the dozens of per-file Resources.Load calls the old
// loose-file layout needed. Plants without a baked sheet cache a null dict so
// the absence check is a single dictionary lookup.
public static class PlantBlobSpriteCache {
    private static readonly Dictionary<string, Dictionary<string, Sprite>> sheets
        = new Dictionary<string, Dictionary<string, Sprite>>();

    // Returns the named sub-sprite (e.g. "g0_static", "g3_b1"), or null if the
    // plant has no baked sheet or the slice doesn't exist.
    public static Sprite Get(string plantName, string spriteName) {
        if (!sheets.TryGetValue(plantName, out var dict)) {
            dict = LoadSheet(plantName);
            sheets[plantName] = dict;   // may be null — caches the "no sheet" answer
        }
        return (dict != null && dict.TryGetValue(spriteName, out var s)) ? s : null;
    }

    private static Dictionary<string, Sprite> LoadSheet(string plantName) {
        var all = Resources.LoadAll<Sprite>(
            "Sprites/Plants/Split/" + plantName + "/" + plantName + "_blobs_baked");
        if (all == null || all.Length == 0) return null;
        var d = new Dictionary<string, Sprite>(all.Length);
        foreach (var s in all) d[s.name] = s;
        return d;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCache() {
        sheets.Clear();
    }
}
