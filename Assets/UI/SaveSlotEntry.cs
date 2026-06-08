using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One row in a save-slot list. Shared by the in-game SaveMenuPanel and the front-end
// menu's MenuLoadPanel — the difference between the two contexts is injected at Init,
// not branched on a singleton, so one prefab + one component serves both.
// Attach to the slotEntryPrefab root. Assign all refs in the prefab Inspector.
//
// Expected prefab children:
//   NameInput  (TMP_InputField)   — editable slot name; EndEdit triggers rename
//   MiceLabel  (TextMeshProUGUI)  — "mice: N"
//   SaveButton (Button)           — overwrite this slot; hidden when showSave is false
//   LoadButton (Button)
public class SaveSlotEntry : MonoBehaviour {
    [Header("Prefab Refs")]
    public TMP_InputField  nameInput;
    public TextMeshProUGUI miceLabel;
    public Button          saveButton;
    public Button          loadButton;
    public Button          deleteButton;

    string _slotName;
    System.Action<string> _onLoad;    // null → default in-game Load (SaveSystem.instance.Load)
    System.Action         _onChanged; // list-rebuild hook after a delete; null → SaveMenuPanel.Refresh

    // Called by the owning panel after instantiation.
    //   startRenaming: immediately focuses the name field (used for newly created slots).
    //   onLoad:        override for the Load button. In-game leaves it null (loads in place);
    //                  the menu passes a handler that hands the slot off to the Main scene.
    //   onChanged:     called after a delete so the owning list can rebuild itself.
    //   showSave:      false hides the per-row Save button (the menu has no world to overwrite).
    public void Init(string slotName, int miceCount, bool startRenaming,
                     System.Action<string> onLoad = null, System.Action onChanged = null, bool showSave = true) {
        _slotName  = slotName;
        _onLoad    = onLoad;
        _onChanged = onChanged;

        if (nameInput == null) { Debug.LogError("SaveSlotEntry: nameInput not assigned"); return; }
        nameInput.text = slotName;
        nameInput.onEndEdit.AddListener(OnRename);

        if (miceLabel != null) miceLabel.text = "mice: " + miceCount;

        if (saveButton != null) {
            saveButton.gameObject.SetActive(showSave);
            if (showSave) saveButton.onClick.AddListener(OnSave);
        } else if (showSave) Debug.LogWarning("SaveSlotEntry: saveButton not assigned on slot: " + slotName);

        if (loadButton != null) loadButton.onClick.AddListener(OnLoad);
        else Debug.LogWarning("SaveSlotEntry: loadButton not assigned on slot: " + slotName);

        if (deleteButton != null) deleteButton.onClick.AddListener(OnDelete);
        else Debug.LogWarning("SaveSlotEntry: deleteButton not assigned on slot: " + slotName);

        if (startRenaming) {
            nameInput.Select();
            nameInput.ActivateInputField();
        }
    }

    void OnRename(string newName) {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName)) { nameInput.text = _slotName; return; }
        if (newName == _slotName) return;
        if (SaveStore.SlotExists(newName)) {
            Debug.LogWarning("SaveSlotEntry: \"" + newName + "\" already exists; reverting.");
            nameInput.text = _slotName;
            return;
        }
        // In-game, route through SaveSystem so the rename follows currentSlot; in the
        // menu there's no SaveSystem, so go straight to the filesystem store.
        bool ok = SaveSystem.instance != null
            ? SaveSystem.instance.RenameSlot(_slotName, newName)
            : SaveStore.RenameSlot(_slotName, newName);
        if (ok) _slotName = newName;
        else nameInput.text = _slotName;
    }

    void OnSave() {
        // If saving to a slot that isn't the currently loaded one, confirm to prevent accidental overwrites.
        if (SaveSystem.instance.currentSlot != _slotName) {
            ConfirmationPopup.Show(
                "overwrite \"" + _slotName + "\"?",
                () => DoSave()
            );
        } else {
            DoSave();
        }
    }

    void DoSave() {
        SaveSystem.instance.Save(_slotName);
        if (miceLabel != null) miceLabel.text = "mice: " + SaveStore.GetAnimalCount(_slotName);
    }

    void OnLoad() {
        // Menu context supplies its own handler (hand the slot off to the Main scene).
        if (_onLoad != null) { _onLoad(_slotName); return; }
        // In-game default: load in place and close the save menu.
        SaveSystem.instance.Load(_slotName);
        if (SaveMenuPanel.instance != null) SaveMenuPanel.instance.gameObject.SetActive(false);
        else Debug.LogError("SaveSlotEntry: SaveMenuPanel.instance is null");
    }

    void OnDelete() {
        string nameAtDeletion = _slotName;
        ConfirmationPopup.Show(
            "really delete \"" + nameAtDeletion + "\"?",
            () => {
                SaveStore.DeleteSlot(nameAtDeletion);
                if (_onChanged != null) _onChanged();
                else if (SaveMenuPanel.instance != null) SaveMenuPanel.instance.Refresh();
                else Debug.LogError("SaveSlotEntry: no refresh hook and SaveMenuPanel.instance is null on delete");
            }
        );
    }
}
