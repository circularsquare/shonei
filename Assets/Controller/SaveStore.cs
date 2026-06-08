using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

// Filesystem-level access to save slots — enumerating, reading metadata (animal
// counts), renaming, deleting. Pure I/O with no dependency on a live World or the
// SaveSystem MonoBehaviour, so the front-end Menu scene (which has neither) can
// list and manage saves before any world is loaded. SaveSystem delegates its slot
// helpers here; the in-game save menu and the menu's load screen share this store.
//
// Animal counts are cached so re-opening a save list doesn't re-stream every file.
// SaveSystem.Save keeps the cache fresh via SetAnimalCount; Delete/Rename below keep
// it consistent. The cache is plain static state — there is one save directory per
// machine, and it never needs a per-world reset.
public static class SaveStore {
    static readonly Dictionary<string, int> animalCountCache = new Dictionary<string, int>();

    public static string SaveDir {
        get {
#if UNITY_EDITOR
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../SaveData"));
#else
            return System.IO.Path.Combine(Application.persistentDataPath, "saves");
#endif
        }
    }

    public static string SlotPath(string slotName) => System.IO.Path.Combine(SaveDir, slotName + ".json");

    // Creates the save directory if it doesn't exist yet. Safe to call repeatedly.
    public static void EnsureDir() {
        if (!System.IO.Directory.Exists(SaveDir)) System.IO.Directory.CreateDirectory(SaveDir);
    }

    // All slot names, newest-modified first.
    public static List<string> GetSaveSlots() {
        var slots = new List<string>();
        if (!System.IO.Directory.Exists(SaveDir)) return slots;
        var files = new System.IO.DirectoryInfo(SaveDir).GetFiles("*.json");
        System.Array.Sort(files, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
        foreach (var fi in files)
            slots.Add(System.IO.Path.GetFileNameWithoutExtension(fi.Name));
        return slots;
    }

    // Name of the most recently modified slot, or null if none exist.
    public static string GetMostRecentSlot() {
        if (!System.IO.Directory.Exists(SaveDir)) return null;
        var files = new System.IO.DirectoryInfo(SaveDir).GetFiles("*.json");
        if (files.Length == 0) return null;
        System.IO.FileInfo newest = files[0];
        for (int i = 1; i < files.Length; i++)
            if (files[i].LastWriteTime > newest.LastWriteTime) newest = files[i];
        return System.IO.Path.GetFileNameWithoutExtension(newest.Name);
    }

    public static bool SlotExists(string slotName) => System.IO.File.Exists(SlotPath(slotName));

    public static void DeleteSlot(string slotName) {
        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("DeleteSlot: slot not found: " + slotName); return; }
        System.IO.File.Delete(path);
        animalCountCache.Remove(slotName);
        Debug.Log("Deleted slot: " + slotName);
    }

    // Renames a save file on disk. Returns true on success. Does NOT touch
    // SaveSystem.currentSlot — that "follow the rename" concern is world state and
    // lives in SaveSystem.RenameSlot, which wraps this.
    public static bool RenameSlot(string oldName, string newName) {
        string oldPath = SlotPath(oldName);
        string newPath = SlotPath(newName);
        if (!System.IO.File.Exists(oldPath)) { Debug.LogError("RenameSlot: source not found: " + oldName); return false; }
        if (System.IO.File.Exists(newPath)) { Debug.LogError("RenameSlot: destination exists: " + newName); return false; }
        System.IO.File.Move(oldPath, newPath);
        if (animalCountCache.TryGetValue(oldName, out int cached)) {
            animalCountCache.Remove(oldName);
            animalCountCache[newName] = cached;
        }
        Debug.Log("Renamed slot \"" + oldName + "\" -> \"" + newName + "\"");
        return true;
    }

    // Records a known animal count for a slot (SaveSystem.Save knows the live count
    // without re-reading the file). Negative count invalidates the cached entry.
    public static void SetAnimalCount(string slotName, int count) {
        if (count < 0) animalCountCache.Remove(slotName);
        else animalCountCache[slotName] = count;
    }

    // Animal count for a slot, cached. On miss, streams the file and counts the
    // top-level objects in the `animals` array without materializing the rest of
    // WorldSaveData (tiles, structures, grids) — that full parse was the source of
    // the save-list-open freeze on large worlds.
    public static int GetAnimalCount(string slotName) {
        if (animalCountCache.TryGetValue(slotName, out int cached)) return cached;

        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("GetAnimalCount: slot not found: " + slotName); return 0; }
        int count = 0;
        try {
            using (var sr = new System.IO.StreamReader(path))
            using (var reader = new JsonTextReader(sr)) {
                while (reader.Read()) {
                    if (reader.TokenType == JsonToken.PropertyName
                        && (string)reader.Value == "animals") {
                        if (!reader.Read() || reader.TokenType != JsonToken.StartArray) break;
                        while (reader.Read() && reader.TokenType != JsonToken.EndArray) {
                            if (reader.TokenType == JsonToken.StartObject) {
                                count++;
                                reader.Skip(); // jump to matching EndObject
                            }
                        }
                        break; // animals array done — no need to read the rest of the file
                    }
                }
            }
        } catch (System.Exception e) {
            Debug.LogError("GetAnimalCount: failed to parse \"" + slotName + "\": " + e.Message);
            return 0;
        }
        animalCountCache[slotName] = count;
        return count;
    }
}
