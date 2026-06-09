using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
// NB: System.IO is fully-qualified throughout — the project defines its own `Path`
// type (pathfinding), which would otherwise shadow System.IO.Path.

// Local bookkeeping for cloud-save sync. Two jobs:
//   1. A stable per-machine GUID (PlayerPrefs) stamped on every upload as `origin`,
//      so a normal two-machine hand-off ("I wrote the current cloud rev") is
//      distinguishable from a real conflict.
//   2. A per-slot marker {uploadedAt, cloudRev} recording what we last pushed and
//      the server rev we last observed, so the menu can decide synced / cloud-newer
//      / conflict without trusting wall clocks. Markers are keyed per account (see
//      ScopedKey) since local saves are now per-account (SaveStore.SaveDir).
//
// The index file lives in persistentDataPath, OUTSIDE SaveStore.SaveDir, so it is
// never picked up by the `*.json` slot glob as a phantom save (build SaveDir is
// persistentDataPath/saves; editor SaveDir is <project>/SaveData — the index sits
// beside neither). Plain static state: one index per machine, no per-world reset.
public static class SaveSyncIndex {
    // What we know about one slot's relationship to the server copy.
    public class Marker {
        public long uploadedAt; // savedAt (unix) of our last successful upload for this slot
        public long cloudRev;   // server-assigned rev we last wrote or observed for this slot
    }

    const string PrefMachineGuid = "savesync.machineGuid";

    static Dictionary<string, Marker> markers;
    static string machineGuid;

    static string IndexPath => System.IO.Path.Combine(Application.persistentDataPath, "savesync-index.json");

    // Markers are keyed per account ("<scope>/<slot>") so two accounts on the same machine
    // with a same-named slot keep independent sync lineages (each tracks ITS cloud copy's
    // rev). One shared index file; the account prefix partitions it. Username and slot names
    // can't contain '/', so the split is unambiguous. The machine GUID stays global — it
    // identifies the install, not the account.
    static string ScopedKey(string slot) => Session.StorageScope + "/" + slot;

    // Stable identifier for this machine/install. Created once, persisted in PlayerPrefs.
    public static string MachineGuid {
        get {
            if (!string.IsNullOrEmpty(machineGuid)) return machineGuid;
            machineGuid = PlayerPrefs.GetString(PrefMachineGuid, "");
            if (string.IsNullOrEmpty(machineGuid)) {
                machineGuid = System.Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(PrefMachineGuid, machineGuid);
                PlayerPrefs.Save();
            }
            return machineGuid;
        }
    }

    static Dictionary<string, Marker> Markers {
        get {
            if (markers != null) return markers;
            markers = new Dictionary<string, Marker>();
            try {
                if (System.IO.File.Exists(IndexPath)) {
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, Marker>>(System.IO.File.ReadAllText(IndexPath));
                    if (loaded != null) markers = loaded;
                }
            } catch (System.Exception e) {
                Debug.LogError("SaveSyncIndex: failed to load index, starting empty: " + e.Message);
            }
            return markers;
        }
    }

    static void Persist() {
        try {
            System.IO.File.WriteAllText(IndexPath, JsonConvert.SerializeObject(Markers, Formatting.Indented));
        } catch (System.Exception e) {
            Debug.LogError("SaveSyncIndex: failed to persist index: " + e.Message);
        }
    }

    // Marker for a slot, or null if we've never synced it.
    public static Marker Get(string slot) {
        return Markers.TryGetValue(ScopedKey(slot), out Marker m) ? m : null;
    }

    // Record a successful upload / observed server state for a slot.
    public static void Set(string slot, long uploadedAt, long cloudRev) {
        Markers[ScopedKey(slot)] = new Marker { uploadedAt = uploadedAt, cloudRev = cloudRev };
        Persist();
    }

    public static void Remove(string slot) {
        if (Markers.Remove(ScopedKey(slot))) Persist();
    }

    // Carry a slot's marker across a rename so the renamed slot keeps its sync lineage.
    public static void Rename(string oldName, string newName) {
        if (Markers.TryGetValue(ScopedKey(oldName), out Marker m)) {
            Markers.Remove(ScopedKey(oldName));
            Markers[ScopedKey(newName)] = m;
            Persist();
        }
    }
}
