using System.Collections.Generic;
using UnityEngine;
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

    void Start() {
        if (instance != null) { Debug.LogError("there should only be one SaveMenuPanel"); }
        instance = this;
    }

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

        List<string> slots = SaveSystem.instance.GetSaveSlots();
        foreach (string slot in slots) {
            int miceCount = SaveSystem.instance.GetAnimalCount(slot);
            GameObject go = Instantiate(slotEntryPrefab, slotList);
            go.name = "SlotEntry_" + slot;
            SaveSlotEntry entry = go.GetComponent<SaveSlotEntry>();
            if (entry == null) { Debug.LogError("SaveMenuPanel: slotEntryPrefab missing SaveSlotEntry component"); continue; }
            entry.Init(slot, miceCount, startRenaming: slot == startRenamingSlot);
        }

        if (saveButton != null) {
            string slot = SaveSystem.instance.currentSlot;
            saveButton.interactable = !string.IsNullOrEmpty(slot) && SaveSystem.instance.SlotExists(slot);
        }
    }

    // Returns "new save", "new save (2)", "new save (3)", etc. — first name with no file on disk.
    string GenerateNewSlotName() {
        string baseName = "new save";
        if (!SaveSystem.instance.SlotExists(baseName)) return baseName;
        int n = 2;
        while (true) {
            string candidate = baseName + " (" + n + ")";
            if (!SaveSystem.instance.SlotExists(candidate)) return candidate;
            n++;
        }
    }
}
