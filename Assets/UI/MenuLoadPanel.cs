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

        // Pull the account's cloud saves (best-effort — offline just yields local-only rows).
        List<SaveSync.CloudMeta> cloud = null;
        if (Session.LoggedIn)
            yield return SaveSync.FetchCloudList((ok, list, err) => { if (ok) cloud = list; });

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
                ConfirmationPopup.Show("cloud copy is newer. load it?\ncancel keeps local",
                    onConfirm: () => StartCoroutine(DownloadThenBoot(slot, meta)),
                    confirmLabel: "cloud",
                    onCancel: () => Boot(slot));
                return;
            case SaveSync.SyncStatus.Conflict:
                ConfirmationPopup.Show("save differs across devices. load cloud?\ncancel loads local",
                    onConfirm: () => StartCoroutine(DownloadThenBoot(slot, meta)),
                    confirmLabel: "cloud",
                    onCancel: () => Boot(slot));
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

    // Hand the chosen slot to the Main scene; WorldController.Start consumes bootSlot
    // and loads it instead of the most-recent save.
    void Boot(string slot) {
        WorldController.bootSlot = slot;
        SceneManager.LoadScene("Main");
    }
}
