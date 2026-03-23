using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Reusable confirmation popup. Call ConfirmationPopup.Show(message, onConfirm) from anywhere.
// onConfirm is only invoked if the user clicks Confirm; Cancel simply closes the popup.
//
// Scene setup:
//   Root "ConfirmationPopup" on Canvas — this script, can be INACTIVE in the scene editor.
//   Child "Blocker" — Image (alpha 0, Raycast Target ON, stretch anchors) absorbs clicks behind popup.
//   Child "Panel" — Image (woodframe.png sliced) centered ~220x90.
//     "MessageText" — TextMeshProUGUI
//     "ConfirmButton" — Button (woodframe.png), text "yes"
//     "CancelButton"  — Button (woodframe.png), text "cancel"
//   Assign messageText, confirmButton, cancelButton in Inspector.
public class ConfirmationPopup : MonoBehaviour {
    public static ConfirmationPopup instance { get; protected set; }

    [Header("Inspector Refs")]
    public TextMeshProUGUI messageText;
    public Button          confirmButton;
    public Button          cancelButton;

    System.Action _onConfirm;
    bool _listenersAdded;

    void Awake() {
        // Runs only if accidentally left active in the scene — hide immediately.
        instance = this;
        gameObject.SetActive(false);
    }

    // Show a confirmation dialog. onConfirm is called only if the user clicks Confirm.
    // confirmLabel defaults to "yes"; pass a custom string for context-specific labels.
    public static void Show(string message, System.Action onConfirm, string confirmLabel = "yes") {
        if (instance == null) {
            // FindObjectOfType(true) searches inactive objects — allows leaving popup inactive in editor.
            instance = FindObjectOfType<ConfirmationPopup>(true);
            if (instance == null) { Debug.LogError("ConfirmationPopup: no instance in scene"); return; }
        }
        if (!instance._listenersAdded) {
            instance.confirmButton.onClick.AddListener(instance.OnConfirm);
            instance.cancelButton.onClick.AddListener(instance.OnCancel);
            instance._listenersAdded = true;
        }
        instance._onConfirm = onConfirm;
        instance.messageText.text = message;
        instance.confirmButton.GetComponentInChildren<TextMeshProUGUI>().text = confirmLabel;
        instance.gameObject.SetActive(true);
        instance.transform.SetAsLastSibling(); // render on top of all other panels
    }

    void OnConfirm() {
        gameObject.SetActive(false);
        _onConfirm?.Invoke();
    }

    void OnCancel() {
        gameObject.SetActive(false);
    }
}
