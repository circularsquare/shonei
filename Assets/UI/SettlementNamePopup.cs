using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Modal shown once on fresh-world creation to let the player name their settlement.
// The game is already paused by WorldController.GenerateDefault when this opens, and the
// Blocker child absorbs background clicks so the world can't be touched behind it.
// Confirming sets World.settlementName (sanitized); skipping (or submitting blank) leaves
// it null → SettlementDisplayName falls back to World.DefaultSettlementName ("new town").
//
// Scene setup (mirror ConfirmationPopup — keep the root INACTIVE in the editor):
//   Root "SettlementNamePopup" on Canvas — this script.
//   Child "Blocker" — Image (alpha ~0, Raycast Target ON, stretch anchors) absorbs clicks behind popup.
//   Child "Panel" — Image (woodframe.png sliced), centered.
//     "TitleText"     — TextMeshProUGUI, "name your settlement"
//     "NameInput"     — TMP_InputField (m5x7 16pt black text; character limit set in code)
//     "ConfirmButton" — Button (woodframe.png), text "confirm"
//     "SkipButton"    — Button (woodframe.png), text "skip"
//   Assign nameInput, confirmButton, skipButton in the Inspector.
public class SettlementNamePopup : MonoBehaviour {
    public static SettlementNamePopup instance { get; protected set; }

    [Header("Inspector Refs")]
    public TMP_InputField nameInput;
    public Button         confirmButton;
    public Button         skipButton;

    bool _listenersAdded;
    bool _showRequested;

    void Awake() {
        instance = this;
        // Hide on startup unless Show() is already activating us (mirrors ConfirmationPopup).
        if (!_showRequested)
            gameObject.SetActive(false);
    }

    // Opens the naming prompt. Callers guard on an empty settlement name, so loaded worlds
    // (which already carry a name) never re-prompt — only the fresh-world path reaches here.
    public static void Show() {
        if (instance == null) {
            // FindObjectOfType(true) searches inactive objects — lets the popup live inactive in the editor.
            instance = FindObjectOfType<SettlementNamePopup>(true);
            if (instance == null) { Debug.LogError("SettlementNamePopup: no instance in scene"); return; }
        }
        if (!instance._listenersAdded) {
            instance.confirmButton.onClick.AddListener(instance.OnConfirm);
            instance.skipButton.onClick.AddListener(instance.OnSkip);
            if (instance.nameInput != null) {
                instance.nameInput.characterLimit = World.MaxSettlementNameLength;
                instance.nameInput.onSubmit.AddListener(_ => instance.OnConfirm()); // Enter confirms
            }
            instance._listenersAdded = true;
        }
        if (instance.nameInput != null) instance.nameInput.text = "";
        instance._showRequested = true;
        instance.gameObject.SetActive(true);
        instance.transform.SetAsLastSibling(); // render on top of all other panels
        if (instance.nameInput != null) {
            instance.nameInput.Select();
            instance.nameInput.ActivateInputField();
        }
    }

    void OnConfirm() {
        // Sanitize collapses blank/illegal input to null, which displays as the default name.
        World.instance.settlementName = World.SanitizeSettlementName(nameInput != null ? nameInput.text : null);
        gameObject.SetActive(false);
    }

    void OnSkip() {
        // Leave settlementName untouched (null on a fresh world) → "new town" fallback.
        gameObject.SetActive(false);
    }
}
