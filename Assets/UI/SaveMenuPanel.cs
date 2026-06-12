using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

// Save/Load menu panel — scrollable per-slot list.
//
// Unity setup:
//   1. Panel "SaveMenuPanel" — attach this script, inactive by default.
//   2. Top bar buttons:
//      - "SaveButton"     → onClick: OnClickSave()       (assign to saveButton field — drives greyed-out state)
//      - "NewSaveButton"  → onClick: OnClickNewSave()
//      - "ResetButton"    → onClick: OnClickReset()
//   3. ScrollRect containing a VerticalLayoutGroup child — assign that child to slotList.
//   4. slotEntryPrefab: prefab with SaveSlotEntry component; assign its 4 child refs in the prefab Inspector.

public class SaveMenuPanel : MonoBehaviour {
    public static SaveMenuPanel instance { get; protected set; }

    [Header("Inspector Refs")]
    public Transform  slotList;        // VerticalLayoutGroup content transform inside ScrollRect
    public GameObject slotEntryPrefab; // prefab with SaveSlotEntry component
    public Button     saveButton;      // optional — interactable only when a slot is currently loaded

    // Live row references, so the cloud-sync badges can be refreshed in place when an
    // upload completes (SaveSync.OnStateChanged) without rebuilding the whole list.
    readonly List<SaveSlotEntry> entries = new List<SaveSlotEntry>();

    void Start() {
        if (instance != null) { Debug.LogError("there should only be one SaveMenuPanel"); }
        instance = this;
    }

    // Subscribe to sync-state changes only while the panel is open (it's inactive by
    // default and toggled), so closed panels do no work.
    void OnEnable()  { SaveSync.OnStateChanged += RefreshSyncBadges; }
    void OnDisable() { SaveSync.OnStateChanged -= RefreshSyncBadges; }

    public void Toggle() {
        bool opening = !gameObject.activeSelf;
        gameObject.SetActive(opening);
        if (opening) RefreshSlotList();
    }

    // -----------------------------------------------------------------------
    // Button handlers (wired in Inspector)
    // -----------------------------------------------------------------------

    // Overwrites the currently-loaded slot. Greyed-out (via saveButton.interactable)
    // when there is no current slot — fresh world, post-reset, or pre-first-save.
    public void OnClickSave() {
        if (SaveSystem.instance == null) { Debug.LogError("SaveMenuPanel: SaveSystem.instance is null"); return; }
        string slot = SaveSystem.instance.currentSlot;
        if (string.IsNullOrEmpty(slot)) { Debug.LogError("SaveMenuPanel: OnClickSave called with no current slot"); return; }
        SaveSystem.instance.Save(slot);
        RefreshSlotList();
    }

    public void OnClickNewSave() {
        if (SaveSystem.instance == null) { Debug.LogError("SaveMenuPanel: SaveSystem.instance is null"); return; }
        string name = GenerateNewSlotName();
        SaveSystem.instance.Save(name);
        RefreshSlotList(startRenamingSlot: name);
    }

    public void OnClickReset() {
        ConfirmationPopup.Show("reset world?", () => {
            gameObject.SetActive(false);
            SaveSystem.instance.LoadDefault();
        }, confirmLabel: "reset");
    }

    // Returns to the front-end menu scene. Confirms first since unsaved progress
    // since the last save is lost (the world isn't auto-persisted on scene change).
    // The logged-in Session is static, so it survives the load — no re-login.
    public void OnClickMainMenu() {
        ConfirmationPopup.Show("main menu? unsaved progress lost", () => {
            SceneManager.LoadScene("Menu");
        }, confirmLabel: "menu");
    }

    // -----------------------------------------------------------------------
    // Slot list
    // -----------------------------------------------------------------------

    // Clears and rebuilds the scroll list. If startRenamingSlot is set, that
    // entry will auto-focus its name input field (used after creating a new save).
    public void Refresh() => RefreshSlotList();

    void RefreshSlotList(string startRenamingSlot = null) {
        if (slotList == null || slotEntryPrefab == null) {
            Debug.LogError("SaveMenuPanel: slotList or slotEntryPrefab not assigned");
            return;
        }
        foreach (Transform child in slotList) Destroy(child.gameObject);
        entries.Clear();

        List<string> slots = SaveStore.GetSaveSlots();
        foreach (string slot in slots) {
            int miceCount = SaveStore.GetAnimalCount(slot);
            GameObject go = Instantiate(slotEntryPrefab, slotList);
            go.name = "SlotEntry_" + slot;
            SaveSlotEntry entry = go.GetComponent<SaveSlotEntry>();
            if (entry == null) { Debug.LogError("SaveMenuPanel: slotEntryPrefab missing SaveSlotEntry component"); continue; }
            entry.Init(slot, miceCount, startRenaming: slot == startRenamingSlot);
            entries.Add(entry);
        }
        RefreshSyncBadges();

        if (saveButton != null) {
            string slot = SaveSystem.instance.currentSlot;
            saveButton.interactable = !string.IsNullOrEmpty(slot) && SaveStore.SlotExists(slot);
        }
    }

    // Re-paint the per-row cloud-sync badge (synced / syncing / local / offline). Cheap and
    // network-free (reads the local marker), so it's safe to call on every sync-state change
    // — a manual save shows "syncing" then flips to "synced" once the upload lands.
    void RefreshSyncBadges() {
        foreach (SaveSlotEntry e in entries) {
            if (e == null) continue; // destroyed mid-rebuild
            e.SetSyncBadge(SaveSync.LocalBadge(e.SlotName, SaveStore.GetSlotModifiedUnix(e.SlotName)));
        }
    }

    // Returns "<settlement>", "<settlement> (2)", etc. — first name with no file on disk.
    // Defaults the base to the settlement name (already slot-safe; "new town" when unnamed)
    // so manual saves are pre-titled after the colony rather than a generic "new save".
    string GenerateNewSlotName() {
        string baseName = World.instance != null ? World.instance.SettlementDisplayName : "new save";
        if (!SaveStore.SlotExists(baseName)) return baseName;
        int n = 2;
        while (true) {
            string candidate = baseName + " (" + n + ")";
            if (!SaveStore.SlotExists(candidate)) return candidate;
            n++;
        }
    }
}
