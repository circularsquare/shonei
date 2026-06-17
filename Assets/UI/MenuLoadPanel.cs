using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Front-end "Load save" screen: a scrollable list of every save slot, reusing the
// same SaveSlot row prefab as the in-game SaveMenuPanel. The list MERGES local disk
// saves with the logged-in account's cloud saves (one row per slot name), each
// badged with its sync state. Picking a slot hands it off to WorldController.bootSlot
// and loads the Main scene; a cloud-only / cloud-newer pick downloads the blob to disk
// first, then boots through the normal local load path.
//
// All UI is scene-authored; MenuController toggles this panel's GameObject and calls
// Refresh() when it opens.
public class MenuLoadPanel : MonoBehaviour {
    public static MenuLoadPanel instance { get; private set; }

    [Header("Inspector Refs")]
    [SerializeField] Transform  slotList;        // VerticalLayoutGroup content inside the ScrollRect
    [SerializeField] GameObject slotEntryPrefab; // SaveSlot.prefab — same row as the in-game save menu
    [SerializeField] GameObject loadingIndicator; // "loading saves..." shown while the cloud list is being fetched (optional)

    void Awake() {
        if (instance != null) { Debug.LogError("there should only be one MenuLoadPanel"); }
        instance = this;
    }

    // Rebuilds the list from disk + cloud. Async because the cloud listing is a network
    // round-trip; the local rows could be shown first, but a save list is small so we
    // just wait for the (fast) /saves call and build the merged list once.
    public void Refresh() {
        StartCoroutine(RefreshRoutine());
    }

    IEnumerator RefreshRoutine() {
        if (slotList == null || slotEntryPrefab == null) {
            Debug.LogError("MenuLoadPanel: slotList or slotEntryPrefab not assigned");
            yield break;
        }
        foreach (Transform child in slotList) Destroy(child.gameObject);
        SetLoading(false);

        // Cloud saves come from the menu's prefetched cache — usually already Ready, so the
        // list builds with no wait. If the prefetch is still running (or hasn't started),
        // show a loading indicator and wait for it. A failed fetch yields local-only rows.
        if (Session.LoggedIn && SaveSync.CloudState != SaveSync.CloudListState.Ready
                             && SaveSync.CloudState != SaveSync.CloudListState.Failed) {
            SetLoading(true);
            yield return SaveSync.WarmCloudList();
            SetLoading(false);
        }
        List<SaveSync.CloudMeta> cloud =
            SaveSync.CloudState == SaveSync.CloudListState.Ready ? SaveSync.CachedCloud : null;

        var cloudByName = new Dictionary<string, SaveSync.CloudMeta>();
        if (cloud != null)
            foreach (var m in cloud)
                if (!m.deleted) cloudByName[m.slot] = m;

        // Union of local + cloud names. Local list is already newest-first; append any
        // cloud-only names after (their rows are tagged "cloud" so order matters less).
        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (string s in SaveStore.GetSaveSlots()) { names.Add(s); seen.Add(s); }
        foreach (var kv in cloudByName) if (!seen.Contains(kv.Key)) names.Add(kv.Key);

        foreach (string slot in names) {
            bool localExists = seen.Contains(slot);
            cloudByName.TryGetValue(slot, out SaveSync.CloudMeta meta);
            var status = SaveSync.ComputeStatus(
                localExists, SaveStore.GetSlotModifiedUnix(slot), meta,
                SaveSyncIndex.Get(slot), SaveSystem.SaveVersion);

            // Mice count: local file if present, else the cloud metadata.
            int miceCount = localExists ? SaveStore.GetAnimalCount(slot)
                                        : (meta != null ? meta.animalCount : 0);

            GameObject go = Instantiate(slotEntryPrefab, slotList);
            go.name = "SlotEntry_" + slot;
            SaveSlotEntry entry = go.GetComponent<SaveSlotEntry>();
            if (entry == null) { Debug.LogError("MenuLoadPanel: slotEntryPrefab missing SaveSlotEntry component"); continue; }

            // Capture per-row context for the load closure.
            string rowSlot = slot;
            SaveSync.SyncStatus rowStatus = status;
            SaveSync.CloudMeta rowMeta = meta;
            entry.Init(rowSlot, miceCount, startRenaming: false,
                       onLoad: _ => LoadChosen(rowSlot, rowStatus, rowMeta),
                       onChanged: Refresh, showSave: false);
            entry.SetSyncStatus(status);
        }
    }

    void SetLoading(bool on) {
        if (loadingIndicator != null) loadingIndicator.SetActive(on);
    }

    // Route a chosen slot to the right load path based on its sync state. Cloud copies
    // are downloaded to disk first, then booted through the normal local path. Conflict
    // and cloud-newer prompt before preferring the cloud copy (never silent).
    void LoadChosen(string slot, SaveSync.SyncStatus status, SaveSync.CloudMeta meta) {
        switch (status) {
            case SaveSync.SyncStatus.NeedsUpdate:
                ConfirmationPopup.Show("\"" + slot + "\" needs a newer game version", null, "ok");
                return;
            case SaveSync.SyncStatus.CloudOnly:
                StartCoroutine(DownloadThenBoot(slot, meta));
                return;
            case SaveSync.SyncStatus.CloudNewer:
                // No "both" here: cloud-newer means local hasn't diverged, so there's
                // nothing unique to preserve.
                ConfirmationPopup.Show("cloud copy is newer\nload which?",
                    onConfirm: () => StartCoroutine(DownloadThenBoot(slot, meta)),
                    confirmLabel: "cloud",
                    onCancel: () => Boot(slot),
                    cancelLabel: "local");
                return;
            case SaveSync.SyncStatus.Conflict:
                ConfirmationPopup.Show("save differs across devices\nload which?",
                    onConfirm: () => StartCoroutine(DownloadThenBoot(slot, meta)),
                    confirmLabel: "cloud",
                    onCancel: () => Boot(slot),
                    cancelLabel: "local",
                    altLabel: "both",
                    onAlt: () => StartCoroutine(KeepBoth(slot, meta)));
                return;
            default: // Synced / LocalOnly / LocalNewer → local is fine
                Boot(slot);
                return;
        }
    }

    // Download a cloud blob, write it to the local slot (so the standard load path picks
    // it up), record the synced rev, then boot.
    IEnumerator DownloadThenBoot(string slot, SaveSync.CloudMeta meta) {
        bool ok = false;
        string json = null, error = null;
        yield return SaveSync.Download(slot, (s, j, e) => { ok = s; json = j; error = e; });
        if (!ok) {
            Debug.LogError("MenuLoadPanel: cloud download of \"" + slot + "\" failed: " + error);
            ConfirmationPopup.Show("couldn't download \"" + slot + "\"", null);
            yield break;
        }
        try {
            SaveStore.EnsureDir();
            System.IO.File.WriteAllText(SaveStore.SlotPath(slot), json);
            if (meta != null) SaveSyncIndex.Set(slot, meta.savedAt, meta.rev);
            SaveStore.SetAnimalCount(slot, -1); // invalidate cache; re-counted on next read
        } catch (System.Exception e) {
            Debug.LogError("MenuLoadPanel: failed to materialize \"" + slot + "\": " + e.Message);
            yield break;
        }
        Boot(slot);
    }

    // Conflict "keep both": preserve the local divergence as its own slot, then adopt
    // the cloud copy under the original name — the cloud rev lineage stays where the
    // server expects it. The set-aside local copy has no cloud counterpart until it's
    // next played and saved, at which point it uploads as a new slot. Doesn't boot:
    // the list refreshes showing both rows so the player picks one deliberately.
    IEnumerator KeepBoth(string slot, SaveSync.CloudMeta meta) {
        // Download FIRST — if the cloud copy can't be fetched, nothing local is touched.
        bool ok = false;
        string json = null, error = null;
        yield return SaveSync.Download(slot, (s, j, e) => { ok = s; json = j; error = e; });
        if (!ok) {
            Debug.LogError("MenuLoadPanel: keep-both download of \"" + slot + "\" failed: " + error);
            ConfirmationPopup.Show("couldn't download \"" + slot + "\"", null);
            yield break;
        }

        // Set the local divergence aside under a dated name. Truncate long bases so the
        // suffixed name stays well under the server's slot-name cap when it later uploads.
        string baseName = slot.Length > 40 ? slot.Substring(0, 40) : slot;
        string copyName = SaveStore.UniqueSlotName(
            baseName + " local " + System.DateTime.Now.ToString("yyyy-MM-dd"));
        if (!SaveStore.RenameSlot(slot, copyName, out _)) {
            ConfirmationPopup.Show("couldn't set aside local copy", null);
            yield break;
        }
        // The copy is an independent lineage — it must not inherit the slot's sync
        // marker (and any stale marker under its name belongs to a long-gone slot).
        SaveSyncIndex.Remove(copyName);

        try {
            System.IO.File.WriteAllText(SaveStore.SlotPath(slot), json);
            SaveStore.SetAnimalCount(slot, -1);
            // Marker from the file's actual mtime so the row badges "synced", not "unsynced".
            SaveSyncIndex.Set(slot, SaveStore.GetSlotModifiedUnix(slot), meta.rev);
        } catch (System.Exception e) {
            Debug.LogError("MenuLoadPanel: keep-both failed to materialize \"" + slot + "\": " + e.Message);
        }
        Refresh();
    }

    // Hand the chosen slot to the Main scene; WorldController.Start consumes bootSlot
    // and loads it instead of the most-recent save.
    void Boot(string slot) {
        WorldController.bootSlot = slot;
        SceneManager.LoadScene("Main");
    }
}
