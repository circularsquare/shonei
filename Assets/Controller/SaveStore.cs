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
// it consistent. The cache is keyed by full slot PATH (not bare slot name) so a slot
// name reused across two accounts' folders can't return a stale count after an account
// switch. Plain static state; never needs a per-world reset.
public static class SaveStore {
    static readonly Dictionary<string, int> animalCountCache = new Dictionary<string, int>();

    // Root holding one subfolder per account. Each account's slots live in its own
    // folder so accounts sharing a machine don't see each other's local saves.
    static string SavesRoot {
        get {
#if UNITY_EDITOR
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../SaveData"));
#else
            return System.IO.Path.Combine(Application.persistentDataPath, "saves");
#endif
        }
    }

    // Active account's save directory — a subfolder of SavesRoot named for the logged-in
    // account (or ".guest" when logged out; see Session.StorageScope). Recomputed from the
    // live Session each access, so logging in/out switches the visible slot set with no
    // explicit refresh hook.
    public static string SaveDir => System.IO.Path.Combine(SavesRoot, Session.StorageScope);

    public static string SlotPath(string slotName) => System.IO.Path.Combine(SaveDir, slotName + ".json");

    // Max slot-name length; mirrors the cloud server's slotRe cap (saves.go).
    public const int MaxSlotNameLength = 64;

    // Validates a slot name against the charset that round-trips everywhere: the cloud
    // server's slotRe (saves.go) = ^[A-Za-z0-9 _-]{1,64}$. A name outside this can't sync
    // to the cloud, and characters like '?' / ':' / '/' also break the local filesystem on
    // some platforms — so the UI checks this before any save or rename and tells the player
    // exactly what's wrong. `error` is a concise player-facing reason on failure (else null).
    public static bool IsValidSlotName(string name, out string error) {
        error = null;
        if (string.IsNullOrEmpty(name)) { error = "name can't be empty"; return false; }
        if (name.Length > MaxSlotNameLength) { error = "name too long (max " + MaxSlotNameLength + ")"; return false; }
        foreach (char c in name) {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9') || c == ' ' || c == '_' || c == '-';
            if (!ok) {
                // Echo printable-ASCII offenders (the common case: ? . / :); for anything
                // else (control/non-ASCII, which m5x7 can't render) fall back to a generic note.
                error = (c >= 32 && c <= 126)
                    ? "name can't use '" + c + "'"
                    : "name has an unsupported character";
                return false;
            }
        }
        return true;
    }

    // Files in SaveDir that are not player save slots. A leading dot marks bookkeeping
    // (e.g. a dot-prefixed sync/index file); excluded from every slot enumeration so it
    // never surfaces as a phantom slot in the load lists or as GetMostRecentSlot's pick.
    static bool IsReservedSlotFile(string fileName) => fileName.StartsWith(".");

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
            if (!IsReservedSlotFile(fi.Name))
                slots.Add(System.IO.Path.GetFileNameWithoutExtension(fi.Name));
        return slots;
    }

    // Name of the most recently modified slot, or null if none exist.
    public static string GetMostRecentSlot() {
        if (!System.IO.Directory.Exists(SaveDir)) return null;
        var files = new System.IO.DirectoryInfo(SaveDir).GetFiles("*.json");
        System.IO.FileInfo newest = null;
        foreach (var fi in files) {
            if (IsReservedSlotFile(fi.Name)) continue;
            if (newest == null || fi.LastWriteTime > newest.LastWriteTime) newest = fi;
        }
        return newest != null ? System.IO.Path.GetFileNameWithoutExtension(newest.Name) : null;
    }

    public static bool SlotExists(string slotName) => System.IO.File.Exists(SlotPath(slotName));

    // First free slot name: "<desired>", then "<desired> 2", "<desired> 3", ...
    public static string UniqueSlotName(string desired) {
        if (!SlotExists(desired)) return desired;
        for (int i = 2; ; i++) {
            string candidate = desired + " " + i;
            if (!SlotExists(candidate)) return candidate;
        }
    }

    // Last-modified time of a slot's file as unix seconds, or 0 if it doesn't exist.
    // Used by cloud-sync status (compared against this machine's last-upload time —
    // both come from this machine's clock, so the comparison is skew-free).
    public static long GetSlotModifiedUnix(string slotName) {
        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) return 0;
        return new System.DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
    }

    public static void DeleteSlot(string slotName) {
        string path = SlotPath(slotName);
        if (!System.IO.File.Exists(path)) { Debug.LogError("DeleteSlot: slot not found: " + slotName); return; }
        System.IO.File.Delete(path);
        animalCountCache.Remove(path);
        Debug.Log("Deleted slot: " + slotName);
    }

    // Renames a save file on disk. Returns true on success. Does NOT touch
    // SaveSystem.currentSlot — that "follow the rename" concern is world state and
    // lives in SaveSystem.RenameSlot, which wraps this.
    // On failure, `error` is set to a short reason the caller can surface to the player
    // (null on success). The store stays UI-free — it never toasts itself, since the
    // front-end Menu scene uses it with no EventFeed; the caller decides how to show it.
    public static bool RenameSlot(string oldName, string newName, out string error) {
        error = null;
        string oldPath = SlotPath(oldName);
        string newPath = SlotPath(newName);
        if (!System.IO.File.Exists(oldPath)) { error = "save not found"; Debug.LogError("RenameSlot: source not found: " + oldName); return false; }
        if (System.IO.File.Exists(newPath)) { error = "name already in use"; Debug.LogError("RenameSlot: destination exists: " + newName); return false; }
        // File.Move throws on an OS-invalid name (e.g. "?" or ":" on Windows), a locked
        // file, or a full disk. Catch it so the rename surfaces as a player-facing toast
        // instead of an unhandled IOException that aborts silently.
        try {
            System.IO.File.Move(oldPath, newPath);
        } catch (System.Exception e) {
            error = e.Message;
            Debug.LogError("RenameSlot: move \"" + oldName + "\" -> \"" + newName + "\" failed: " + e);
            return false;
        }
        if (animalCountCache.TryGetValue(oldPath, out int cached)) {
            animalCountCache.Remove(oldPath);
            animalCountCache[newPath] = cached;
        }
        Debug.Log("Renamed slot \"" + oldName + "\" -> \"" + newName + "\"");
        return true;
    }

    // Records a known animal count for a slot (SaveSystem.Save knows the live count
    // without re-reading the file). Negative count invalidates the cached entry.
    public static void SetAnimalCount(string slotName, int count) {
        string path = SlotPath(slotName);
        if (count < 0) animalCountCache.Remove(path);
        else animalCountCache[path] = count;
    }

    // Animal count for a slot, cached. On miss, streams the file and counts the
    // top-level objects in the `animals` array without materializing the rest of
    // WorldSaveData (tiles, structures, grids) — that full parse was the source of
    // the save-list-open freeze on large worlds.
    public static int GetAnimalCount(string slotName) {
        string path = SlotPath(slotName);
        if (animalCountCache.TryGetValue(path, out int cached)) return cached;

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
        animalCountCache[path] = count;
        return count;
    }
}
