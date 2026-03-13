using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Save/Load/Reset menu panel.
//
// Unity setup required:
//   1. Create a GameObject "SaveSystem" in the scene, attach SaveSystem.cs to it.
//   2. Create a UI Button (bottom-right corner) that calls SaveMenuPanel.Toggle().
//   3. Create a Panel "SaveMenuPanel", attach this script to it, set inactive by default.
//   4. Inside the panel:
//      - An InputField named "SlotNameInput" (TMP_InputField) for typing slot names.
//      - A Button "SaveButton"   -> onClick calls OnClickSave()
//      - A Button "LoadButton"   -> onClick calls OnClickLoad()
//      - A Button "ResetButton"  -> onClick calls OnClickReset()
//      - A ScrollRect/VerticalLayoutGroup "SlotList" for listing existing save slots.
//   5. Assign slotEntryPrefab: a prefab with a Button whose child text shows the slot name;
//      clicking it populates the input field.

public class SaveMenuPanel : MonoBehaviour {
    public static SaveMenuPanel instance;

    public TMP_InputField slotNameInput;
    public Transform slotList;          // parent for slot entry buttons
    public GameObject slotEntryPrefab;  // prefab: Button with TMP child text
    public TextMeshProUGUI feedbackText;// optional: shows "Saved!" / "Loaded!" messages

    void Start() {
        if (instance != null) { Debug.LogError("there should only be one SaveMenuPanel"); }
        instance = this;
        //gameObject.SetActive(false);
    }

    public void Toggle() {
        bool opening = !gameObject.activeSelf;
        gameObject.SetActive(opening);
        if (opening) RefreshSlotList();
    }

    public void OnClickSave() {
        string slot;
        if (slotNameInput == null) {slot = "default";}
        else {slot = slotNameInput.text.Trim();}
        SaveSystem.instance.Save(slot);
        SetFeedback("Saved to \"" + slot + "\".");
        RefreshSlotList();
    }

    public void OnClickLoad() {
        string slot;
        if (slotNameInput == null) {slot = "default";}
        else {slot = slotNameInput.text.Trim();}
        if (!SaveSystem.instance.SlotExists(slot)) { SetFeedback("Slot \"" + slot + "\" not found."); return; }
        SaveSystem.instance.Load(slot);
        gameObject.SetActive(false);
    }

    public void OnClickReset() {
        SaveSystem.instance.Reset();
    }

    void RefreshSlotList() {
        if (slotList == null || slotEntryPrefab == null) return;
        foreach (Transform child in slotList) Destroy(child.gameObject);

        List<string> slots = SaveSystem.instance.GetSaveSlots();
        foreach (string slot in slots) {
            string captured = slot;
            GameObject entry = Instantiate(slotEntryPrefab, slotList);
            entry.name = "Slot_" + slot;
            TextMeshProUGUI label = entry.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = slot;
            Button btn = entry.GetComponent<Button>();
            if (btn != null) btn.onClick.AddListener(() => slotNameInput.text = captured);
        }
    }

    void SetFeedback(string msg) {
        if (feedbackText != null) feedbackText.text = msg;
        else Debug.Log("[SaveMenu] " + msg);
    }
}
