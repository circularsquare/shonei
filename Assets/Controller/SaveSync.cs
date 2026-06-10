using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
// NB: System.Threading.Tasks.Task and System.IO.Compression.CompressionLevel are
// fully-qualified at use sites — the project defines its own `Task` type (the
// objective queue) and UnityEngine has a `CompressionLevel`, both of which would
// otherwise collide.

// Client half of account-owned cloud saves (server: shonei-server/saves.go).
//
// The local save stays authoritative; this is an async MIRROR. SaveSystem.Save
// writes the file synchronously, then hands the already-serialized JSON here via
// QueueUpload — the network is never on the save's critical path. Uploads gzip on
// a worker thread and PUT in the background.
//
// LIFECYCLE: the upload pump must outlive the Menu->Main scene load (a final save
// fires right as the player returns to the menu), so it runs on a dedicated
// DontDestroyOnLoad runner — NOT on SaveSystem, which is a Main-scene-only object.
// The list/download/delete coroutines are caller-driven (the menu drives them via
// its own StartCoroutine, exactly like AuthClient).
public static class SaveSync {
    public enum SyncState { Idle, Syncing, Offline, Error }
    public static SyncState State { get; private set; }
    public static event Action OnStateChanged;

    // Per-slot relationship between the local file and the account's cloud copy,
    // shown as a badge in the menu Load list. Decided rev-first (see ComputeStatus).
    public enum SyncStatus {
        Unknown,     // no info (e.g. logged out, or in-game where everything is local)
        LocalOnly,   // on disk here, not in the cloud
        CloudOnly,   // in the cloud, not on disk here
        Synced,      // identical lineage, neither side moved since last sync
        LocalNewer,  // local edited since last upload; pump will push it
        CloudNewer,  // cloud advanced since we last synced; local is stale
        Conflict,    // both moved since last sync (or independent lineages) — player chooses
        NeedsUpdate, // cloud save's schema is newer than this build supports
    }

    // Classify a slot from local presence + cloud metadata + our local marker. Uses
    // server-assigned `rev` (not wall clocks) as the source of truth for "did the
    // cloud move", and the local file mtime vs our last-upload time for "did we edit
    // locally". A missing marker with both copies present = independent lineages we
    // can't reconcile → Conflict.
    public static SyncStatus ComputeStatus(bool localExists, long localModifiedUnix,
                                           CloudMeta cloud, SaveSyncIndex.Marker marker,
                                           int supportedSaveVersion) {
        if (cloud == null) return localExists ? SyncStatus.LocalOnly : SyncStatus.Unknown;
        if (cloud.saveVersion > supportedSaveVersion) return SyncStatus.NeedsUpdate;
        if (!localExists) return SyncStatus.CloudOnly;
        if (marker == null) return SyncStatus.Conflict;
        bool cloudMoved = cloud.rev > marker.cloudRev;
        bool localDirty = localModifiedUnix > marker.uploadedAt;
        if (cloudMoved && localDirty) return SyncStatus.Conflict;
        if (cloudMoved) return SyncStatus.CloudNewer;
        if (localDirty) return SyncStatus.LocalNewer;
        return SyncStatus.Synced;
    }

    // Network-free per-slot badge for the IN-GAME save menu: reflects only whether THIS
    // machine has uploaded the current local version (from the local marker + file mtime
    // + live pump state). Cross-device "cloud newer" / conflict is a menu/Load concern and
    // intentionally not surfaced in-game. Empty when logged out (no cloud at all).
    public static string LocalBadge(string slot, long localModifiedUnix) {
        if (!Session.LoggedIn) return "";
        if (pending.ContainsKey(slot)) return "syncing"; // queued / uploading this slot
        SaveSyncIndex.Marker m = SaveSyncIndex.Get(slot);
        if (m != null && localModifiedUnix <= m.uploadedAt) return "synced"; // current version is up
        if (State == SyncState.Offline) return "offline"; // tried, couldn't reach the server
        return "local"; // not uploaded yet
    }

    // Concise, ASCII-only badge text (m5x7 has no non-ASCII glyphs). Empty = no badge.
    public static string StatusText(SyncStatus s) {
        switch (s) {
            case SyncStatus.LocalOnly:   return "local";
            case SyncStatus.CloudOnly:   return "cloud";
            case SyncStatus.Synced:      return "synced";
            case SyncStatus.LocalNewer:  return "unsynced";
            case SyncStatus.CloudNewer:  return "cloud newer";
            case SyncStatus.Conflict:    return "conflict";
            case SyncStatus.NeedsUpdate: return "update needed";
            default:                     return "";
        }
    }

    // Raised when the server rejects our token (401). The session has lapsed; the UI
    // should prompt re-login. The upload pump stops retrying until a fresh login.
    public static event Action OnAuthExpired;

    // One queued upload (latest-wins per slot — a newer save supersedes an older
    // one still waiting). json is the raw serialized world; gzip happens at send time.
    class Pending {
        public string json;
        public long   savedAt;
        public int    animals;
        public int    saveVersion;
    }

    // Server's per-slot metadata (mirror of saves.go SaveMeta).
    [Serializable]
    public class CloudMeta {
        public string slot;
        public long   rev;
        public long   savedAt;
        public long   sizeGz;
        public int    animalCount;
        public int    saveVersion;
        public string origin;
        public bool   deleted;
    }

    const float IdlePollSeconds    = 0.5f;  // how often the pump checks for work when idle
    const float NetworkRetrySeconds = 15f;  // backoff after a transient upload failure

    // Touched only on the main thread (QueueUpload and the pump coroutine both run there).
    static readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
    static bool authStopped; // set on 401 — pump idles until a fresh login clears it

    // ── Upload (background, via the runner) ────────────────────────────────────

    // Called by SaveSystem.Save after the local write. Non-blocking; coalesces by slot.
    public static void QueueUpload(string slot, string json, long savedAt, int animals, int saveVersion) {
        if (!Session.LoggedIn) return;
        authStopped = false; // a fresh save implies we want to try again
        pending[slot] = new Pending { json = json, savedAt = savedAt, animals = animals, saveVersion = saveVersion };
        SaveSyncRunner.Ensure(); // make sure the pump is running
    }

    // The pump loop, started by the runner. Drains `pending` one slot at a time.
    internal static IEnumerator UploadPump() {
        while (true) {
            if (authStopped || pending.Count == 0 || !Session.LoggedIn) {
                if (pending.Count == 0 && State == SyncState.Syncing) SetState(SyncState.Idle);
                yield return new WaitForSecondsRealtime(IdlePollSeconds);
                continue;
            }

            // Take one slot (snapshot the value so a concurrent QueueUpload can supersede it).
            string slot = null;
            foreach (var k in pending.Keys) { slot = k; break; }
            Pending up = pending[slot];
            SetState(SyncState.Syncing);

            // Gzip off the main thread so a 2 MB save doesn't stack onto the save-frame hitch.
            System.Threading.Tasks.Task<byte[]> gz = System.Threading.Tasks.Task.Run(() => Gzip(up.json));
            while (!gz.IsCompleted) yield return null;
            if (gz.IsFaulted) {
                Debug.LogError("SaveSync: gzip failed for \"" + slot + "\": " + gz.Exception?.GetBaseException().Message);
                pending.Remove(slot);
                continue;
            }

            UploadResult result = UploadResult.NetworkError;
            yield return Put(slot, gz.Result, up, r => result = r);

            switch (result) {
                case UploadResult.Ok:
                    // Only clear if not superseded by a newer queued save while we uploaded.
                    if (pending.TryGetValue(slot, out Pending cur) && cur.savedAt == up.savedAt)
                        pending.Remove(slot);
                    SetState(pending.Count == 0 ? SyncState.Idle : SyncState.Syncing);
                    break;
                case UploadResult.AuthExpired:
                    authStopped = true;
                    SetState(SyncState.Error);
                    OnAuthExpired?.Invoke();
                    break;
                case UploadResult.DeletedElsewhere:
                    // The slot was tombstoned on another device; drop our upload.
                    Debug.Log("SaveSync: \"" + slot + "\" was deleted on another device; dropping upload.");
                    pending.Remove(slot);
                    SaveSyncIndex.Remove(slot);
                    break;
                case UploadResult.QuotaExceeded:
                    Debug.LogError("SaveSync: cloud quota exceeded uploading \"" + slot + "\"; dropping.");
                    pending.Remove(slot);
                    break;
                default: // NetworkError / ServerError → keep it queued, back off
                    SetState(SyncState.Offline);
                    yield return new WaitForSecondsRealtime(NetworkRetrySeconds);
                    break;
            }
        }
    }

    enum UploadResult { Ok, AuthExpired, DeletedElsewhere, QuotaExceeded, NetworkError, ServerError }

    static IEnumerator Put(string slot, byte[] gzBody, Pending up, Action<UploadResult> done) {
        SaveSyncIndex.Marker marker = SaveSyncIndex.Get(slot);
        long baseRev = marker != null ? marker.cloudRev : 0;
        string url = MarketServer.HttpBase + "/save"
            + "?slot="    + UnityWebRequest.EscapeURL(slot)
            + "&baseRev=" + baseRev
            + "&savedAt=" + up.savedAt
            + "&animals=" + up.animals
            + "&sv="      + up.saveVersion
            + "&origin="  + UnityWebRequest.EscapeURL(SaveSyncIndex.MachineGuid);

        using (var req = new UnityWebRequest(url, "PUT")) {
            req.uploadHandler   = new UploadHandlerRaw(gzBody);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", "Bearer " + Session.Token);
            req.SetRequestHeader("Content-Type", "application/octet-stream");
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success) {
                // Record the server-assigned rev so future conflict checks are clock-free.
                var meta = TryParse<CloudMeta>(req.downloadHandler.text);
                long rev = meta != null ? meta.rev : baseRev + 1;
                SaveSyncIndex.Set(slot, up.savedAt, rev);
                done(UploadResult.Ok);
                yield break;
            }
            done(Classify(req));
        }
    }

    // ── Cached account cloud listing (menu prefetch) ───────────────────────────

    // The menu warms this once when it appears so the Continue button and the Load list
    // don't each pay a fresh /saves round-trip — and, critically, so a FAILED fetch is
    // distinguishable from a genuinely empty account. The old Continue flow conflated both
    // as "nothing to load" and silently started a new world; with explicit Failed state the
    // caller can keep the player on the menu instead. State changes fire OnCloudListChanged.
    public enum CloudListState { None, Fetching, Ready, Failed }
    public static CloudListState CloudState { get; private set; }
    public static List<CloudMeta> CachedCloud { get; private set; }
    public static event Action OnCloudListChanged;

    static string cloudCacheScope; // account the cache was fetched for; a scope change invalidates it

    // Kick off (or refresh) the cached cloud listing for the logged-in account. Coroutine:
    // yield it to be sure the result is in hand. Reuses an in-flight fetch (waits for it)
    // and a Ready cache (returns immediately) unless force is set. Logged out → None.
    public static IEnumerator WarmCloudList(bool force = false) {
        if (!Session.LoggedIn) {
            CachedCloud = null; cloudCacheScope = null;
            SetCloudState(CloudListState.None);
            yield break;
        }
        // Account switched since the last fetch — the cache belongs to a different account.
        if (cloudCacheScope != Session.StorageScope) {
            CachedCloud = null; cloudCacheScope = Session.StorageScope;
            SetCloudState(CloudListState.None);
        }
        // A prefetch is already running — wait for it rather than firing a duplicate.
        if (CloudState == CloudListState.Fetching) {
            while (CloudState == CloudListState.Fetching) yield return null;
            if (!force) yield break;
        }
        if (!force && CloudState == CloudListState.Ready) yield break;

        SetCloudState(CloudListState.Fetching);
        bool ok = false; List<CloudMeta> list = null;
        yield return FetchCloudList((s, l, e) => { ok = s; list = l; });
        // Guard against an account switch that happened while the request was in flight.
        if (cloudCacheScope != Session.StorageScope) yield break;
        if (ok) { CachedCloud = list; SetCloudState(CloudListState.Ready); }
        else    { SetCloudState(CloudListState.Failed); }
    }

    static void SetCloudState(CloudListState s) {
        CloudState = s;
        OnCloudListChanged?.Invoke();
    }

    // ── List / download / delete (caller-driven, like AuthClient) ──────────────

    // GET /saves → the account's live (non-tombstoned) slot metadata. done(ok, list, error).
    public static IEnumerator FetchCloudList(Action<bool, List<CloudMeta>, string> done) {
        if (!Session.LoggedIn) { done(false, null, "not logged in"); yield break; }
        using (var req = UnityWebRequest.Get(MarketServer.HttpBase + "/saves")) {
            req.SetRequestHeader("Authorization", "Bearer " + Session.Token);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) {
                done(false, null, req.responseCode == 401 ? "session expired" : "can't reach server");
                yield break;
            }
            var list = TryParse<List<CloudMeta>>(req.downloadHandler.text) ?? new List<CloudMeta>();
            done(true, list, null);
        }
    }

    // GET /save?slot= → the decompressed world JSON. done(ok, json, error).
    public static IEnumerator Download(string slot, Action<bool, string, string> done) {
        if (!Session.LoggedIn) { done(false, null, "not logged in"); yield break; }
        string url = MarketServer.HttpBase + "/save?slot=" + UnityWebRequest.EscapeURL(slot);
        using (var req = UnityWebRequest.Get(url)) {
            req.SetRequestHeader("Authorization", "Bearer " + Session.Token);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) {
                done(false, null, req.responseCode == 404 ? "no cloud copy" : "download failed");
                yield break;
            }
            string json;
            try { json = Gunzip(req.downloadHandler.data); }
            catch (Exception e) { done(false, null, "corrupt download: " + e.Message); yield break; }
            done(true, json, null);
        }
    }

    // DELETE /save?slot= → tombstone the slot server-side. done(ok, error).
    public static IEnumerator DeleteRemote(string slot, Action<bool, string> done) {
        if (!Session.LoggedIn) { done(false, "not logged in"); yield break; }
        string url = MarketServer.HttpBase + "/save?slot=" + UnityWebRequest.EscapeURL(slot);
        using (var req = UnityWebRequest.Delete(url)) {
            req.SetRequestHeader("Authorization", "Bearer " + Session.Token);
            yield return req.SendWebRequest();
            bool ok = req.result == UnityWebRequest.Result.Success;
            if (ok) SaveSyncIndex.Remove(slot);
            done(ok, ok ? null : "delete failed");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    static UploadResult Classify(UnityWebRequest req) {
        switch (req.responseCode) {
            case 401: return UploadResult.AuthExpired;
            case 409: return UploadResult.DeletedElsewhere;
            case 413: return UploadResult.QuotaExceeded;
            default:
                return req.result == UnityWebRequest.Result.ProtocolError
                    ? UploadResult.ServerError : UploadResult.NetworkError;
        }
    }

    static void SetState(SyncState s) {
        if (State == s) return;
        State = s;
        OnStateChanged?.Invoke();
    }

    static T TryParse<T>(string json) where T : class {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonConvert.DeserializeObject<T>(json); }
        catch { return null; }
    }

    static byte[] Gzip(string s) {
        byte[] raw = Encoding.UTF8.GetBytes(s);
        using (var ms = new MemoryStream()) {
            using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }
    }

    static string Gunzip(byte[] data) {
        using (var ms = new MemoryStream(data))
        using (var gz = new GZipStream(ms, CompressionMode.Decompress))
        using (var outMs = new MemoryStream()) {
            gz.CopyTo(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray());
        }
    }
}
