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
    public TextMeshProUGUI syncLabel;   // cloud-sync badge; only the menu sets it (in-game: blank)
    public Button          saveButton;
    public Button          loadButton;
    public Button          deleteButton;

    string _slotName;
    public string SlotName => _slotName;
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

    // Sets the cloud-sync badge from a full status (menu's MenuLoadPanel — network-aware).
    // A cloud-only row has no local file to rename, so its name field is locked.
    public void SetSyncStatus(SaveSync.SyncStatus status) {
        SetSyncBadge(SaveSync.StatusText(status));
        if (status == SaveSync.SyncStatus.CloudOnly && nameInput != null)
            nameInput.interactable = false;
    }

    // Sets the badge text directly (in-game SaveMenuPanel uses the network-free LocalBadge).
    public void SetSyncBadge(string text) {
        if (syncLabel != null) syncLabel.text = text;
    }

    void OnRename(string newName) {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName)) { nameInput.text = _slotName; return; }
        if (newName == _slotName) return;
        // Reject illegal characters up front with a specific message (e.g. "name can't use
        // '?'"), so the player learns what's wrong instead of hitting a cryptic IOException.
        if (!SaveStore.IsValidSlotName(newName, out string nameErr)) {
            EventFeed.instance?.Post("<color=#cc3333>" + nameErr + "</color>");
            nameInput.text = _slotName;
            return;
        }
        if (SaveStore.SlotExists(newName)) {
            EventFeed.instance?.Post("<color=#cc3333>\"" + newName + "\" already exists</color>");
            nameInput.text = _slotName;
            return;
        }
        string oldName = _slotName;
        // In-game, route through SaveSystem so the rename follows currentSlot; in the
        // menu there's no SaveSystem, so go straight to the filesystem store.
        string error;
        bool ok = SaveSystem.instance != null
            ? SaveSystem.instance.RenameSlot(oldName, newName, out error)
            : SaveStore.RenameSlot(oldName, newName, out error);
        if (ok) { _slotName = newName; PropagateRenameToCloud(oldName, newName); }
        else {
            // Surface the failure (e.g. an illegal character in the name) instead of
            // silently snapping the text back. EventFeed is null in the Menu scene, where
            // the reverted name field is the only available signal.
            EventFeed.instance?.Post("<color=#cc3333>rename failed: " + error + "</color>");
            nameInput.text = _slotName;
        }
    }

    // Keep the account's cloud copy consistent with a local rename: re-upload the file
    // under its new name and tombstone the old name. No-op when logged out. The new
    // slot's cloud copy lands on the next pump tick; the old name is tombstoned so it
    // doesn't linger as a stale "cloud only" row.
    void PropagateRenameToCloud(string oldName, string newName) {
        if (!Session.LoggedIn) return;
        SaveSyncIndex.Rename(oldName, newName);
        try {
            string json = System.IO.File.ReadAllText(SaveStore.SlotPath(newName));
            SaveSync.QueueUpload(newName, json, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                 SaveStore.GetAnimalCount(newName), SaveSystem.SaveVersion);
        } catch (System.Exception e) {
            Debug.LogError("SaveSlotEntry: cloud re-upload on rename failed: " + e.Message);
        }
        SaveSyncRunner.Run(SaveSync.DeleteRemote(oldName, (cloudOk, err) => {
            if (!cloudOk) Debug.LogWarning("SaveSlotEntry: cloud delete of old name failed: " + err);
        }));
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
        if (!SaveSystem.instance.Save(_slotName)) return; // failed save already toasted
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
                // Cloud-only rows have no local file — skip the local delete (it would
                // just LogError) but still tombstone the cloud copy below.
                if (SaveStore.SlotExists(nameAtDeletion)) SaveStore.DeleteSlot(nameAtDeletion);
                // Propagate to the cloud as a tombstone so the delete sticks even if
                // another machine is offline (it can't resurrect a tombstoned slot).
                // Detached on the runner so it survives the list rebuild below.
                if (Session.LoggedIn)
                    SaveSyncRunner.Run(SaveSync.DeleteRemote(nameAtDeletion, (cloudOk, err) => {
                        if (!cloudOk) Debug.LogWarning("SaveSlotEntry: cloud delete failed: " + err);
                    }));
                if (_onChanged != null) _onChanged();
                else if (SaveMenuPanel.instance != null) SaveMenuPanel.instance.Refresh();
                else Debug.LogError("SaveSlotEntry: no refresh hook and SaveMenuPanel.instance is null on delete");
            }
        );
    }
}
