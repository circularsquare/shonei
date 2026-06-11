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
    public Button          altButton;      // optional third choice (e.g. conflict "both"); may be unassigned

    System.Action _onConfirm;
    System.Action _onCancel;
    System.Action _onAlt;
    bool _listenersAdded;
    bool _showRequested;

    void Awake() {
        instance = this;
        // Hide on startup unless Show() is already activating us.
        if (!_showRequested)
            gameObject.SetActive(false);
    }

    // Show a confirmation dialog. onConfirm is called only if the user clicks Confirm.
    // onCancel (optional) is called if the user clicks Cancel — use it when Cancel means
    // "do the other thing" rather than "do nothing". confirmLabel defaults to "yes".
    // altLabel + onAlt (optional) show a third button for genuine three-way choices
    // (e.g. save conflict cloud / local / both) — requires altButton assigned in the scene.
    public static void Show(string message, System.Action onConfirm, string confirmLabel = "yes",
                            System.Action onCancel = null, string cancelLabel = "cancel",
                            string altLabel = null, System.Action onAlt = null) {
        if (instance == null) {
            // FindObjectOfType(true) searches inactive objects — allows leaving popup inactive in editor.
            instance = FindObjectOfType<ConfirmationPopup>(true);
            if (instance == null) { Debug.LogError("ConfirmationPopup: no instance in scene"); return; }
        }
        if (!instance._listenersAdded) {
            instance.confirmButton.onClick.AddListener(instance.OnConfirm);
            instance.cancelButton.onClick.AddListener(instance.OnCancel);
            if (instance.altButton != null) instance.altButton.onClick.AddListener(instance.OnAlt);
            instance._listenersAdded = true;
        }
        instance._onConfirm = onConfirm;
        instance._onCancel = onCancel;
        instance._onAlt = onAlt;
        instance.messageText.text = message;
        instance.confirmButton.GetComponentInChildren<TextMeshProUGUI>().text = confirmLabel;
        instance.cancelButton.GetComponentInChildren<TextMeshProUGUI>().text = cancelLabel;

        bool useAlt = onAlt != null && !string.IsNullOrEmpty(altLabel);
        if (useAlt && instance.altButton == null) {
            // Degrades to the two-button dialog; the alt choice is simply unavailable.
            Debug.LogError("ConfirmationPopup: alt option \"" + altLabel + "\" requested but altButton not assigned in this scene");
            useAlt = false;
        }
        if (instance.altButton != null) {
            instance.altButton.gameObject.SetActive(useAlt);
            if (useAlt) instance.altButton.GetComponentInChildren<TextMeshProUGUI>().text = altLabel;
        }

        instance._showRequested = true;
        instance.gameObject.SetActive(true);
        instance.transform.SetAsLastSibling(); // render on top of all other panels
    }

    void OnConfirm() {
        gameObject.SetActive(false);
        _onConfirm?.Invoke();
    }

    void OnCancel() {
        gameObject.SetActive(false);
        _onCancel?.Invoke();
    }

    void OnAlt() {
        gameObject.SetActive(false);
        _onAlt?.Invoke();
    }
}
