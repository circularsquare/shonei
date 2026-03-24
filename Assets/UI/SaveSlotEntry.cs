using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One row in the SaveMenuPanel slot list.
// Attach to the slotEntryPrefab root. Assign all refs in the prefab Inspector.
//
// Expected prefab children:
//   NameInput  (TMP_InputField)   — editable slot name; EndEdit triggers rename
//   MiceLabel  (TextMeshProUGUI)  — "mice: N"
//   SaveButton (Button)
//   LoadButton (Button)
public class SaveSlotEntry : MonoBehaviour {
    [Header("Prefab Refs")]
    public TMP_InputField  nameInput;
    public TextMeshProUGUI miceLabel;
    public Button          saveButton;
    public Button          loadButton;
    public Button          deleteButton;

    string _slotName;

    // Called by SaveMenuPanel after instantiation.
    // startRenaming: immediately focuses the name field (used for newly created slots).
    public void Init(string slotName, int miceCount, bool startRenaming) {
        _slotName = slotName;

        if (nameInput == null) { Debug.LogError("SaveSlotEntry: nameInput not assigned"); return; }
        nameInput.text = slotName;
        nameInput.onEndEdit.AddListener(OnRename);

        if (miceLabel != null) miceLabel.text = "mice: " + miceCount;

        if (saveButton != null) saveButton.onClick.AddListener(OnSave);
        else Debug.LogWarning("SaveSlotEntry: saveButton not assigned on slot: " + slotName);

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
        if (SaveSystem.instance.SlotExists(newName)) {
            Debug.LogWarning("SaveSlotEntry: \"" + newName + "\" already exists; reverting.");
            nameInput.text = _slotName;
            return;
        }
        bool ok = SaveSystem.instance.RenameSlot(_slotName, newName);
        if (ok) _slotName = newName;
        else nameInput.text = _slotName;
    }

    void OnSave() {
        SaveSystem.instance.Save(_slotName);
        if (miceLabel != null) miceLabel.text = "mice: " + SaveSystem.instance.GetAnimalCount(_slotName);
    }

    void OnLoad() {
        SaveSystem.instance.Load(_slotName);
        if (SaveMenuPanel.instance != null) SaveMenuPanel.instance.gameObject.SetActive(false);
        else Debug.LogError("SaveSlotEntry: SaveMenuPanel.instance is null");
    }

    void OnDelete() {
        string nameAtDeletion = _slotName;
        ConfirmationPopup.Show(
            "really delete \"" + nameAtDeletion + "\"?",
            () => {
                SaveSystem.instance.DeleteSlot(nameAtDeletion);
                if (SaveMenuPanel.instance != null) SaveMenuPanel.instance.Refresh();
                else Debug.LogError("SaveSlotEntry: SaveMenuPanel.instance is null on delete");
            }
        );
    }
}
