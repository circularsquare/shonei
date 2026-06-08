using UnityEngine;
using UnityEngine.SceneManagement;

// Front-end "Load save" screen: a scrollable list of every save slot, reusing the
// same SaveSlot row prefab as the in-game SaveMenuPanel. Picking a slot hands it off
// to WorldController.bootSlot and loads the Main scene (the menu has no live World to
// load into directly). Delete/rename operate on disk via SaveStore; there is no
// per-row Save button — there's no world to overwrite before one is loaded.
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

    // Clears and rebuilds the scroll list from the saves on disk.
    public void Refresh() {
        if (slotList == null || slotEntryPrefab == null) {
            Debug.LogError("MenuLoadPanel: slotList or slotEntryPrefab not assigned");
            return;
        }
        foreach (Transform child in slotList) Destroy(child.gameObject);

        foreach (string slot in SaveStore.GetSaveSlots()) {
            int miceCount = SaveStore.GetAnimalCount(slot);
            GameObject go = Instantiate(slotEntryPrefab, slotList);
            go.name = "SlotEntry_" + slot;
            SaveSlotEntry entry = go.GetComponent<SaveSlotEntry>();
            if (entry == null) { Debug.LogError("MenuLoadPanel: slotEntryPrefab missing SaveSlotEntry component"); continue; }
            entry.Init(slot, miceCount, startRenaming: false, onLoad: LoadSlot, onChanged: Refresh, showSave: false);
        }
    }

    // Hand the chosen slot to the Main scene; WorldController.Start consumes bootSlot
    // and loads it instead of the most-recent save.
    void LoadSlot(string slot) {
        WorldController.bootSlot = slot;
        SceneManager.LoadScene("Main");
    }
}
