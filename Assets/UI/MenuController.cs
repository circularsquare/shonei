using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Controller for the front-end menu scene (Menu.unity), the first scene the game
// loads. Drives two panels: a login/register form, and — once authenticated — a
// main menu (Play / New Game / Log Out / Quit) that hands off to the Main scene.
//
// All UI is scene-authored; this script only holds [SerializeField] refs and the
// button handlers. Wire the buttons' onClick to the On* methods in the editor.
public class MenuController : MonoBehaviour {
    [Header("Login panel")]
    [SerializeField] GameObject      loginPanel;
    [SerializeField] TMP_InputField  usernameInput;
    [SerializeField] TMP_InputField  passwordInput;
    [SerializeField] Toggle          rememberToggle;   // optional
    [SerializeField] TextMeshProUGUI statusText;        // login errors / progress

    [Header("Main menu panel")]
    [SerializeField] GameObject      mainPanel;
    [SerializeField] TextMeshProUGUI loggedInLabel;     // "logged in as <name>"
    [SerializeField] Button          continueButton;    // disabled when no saves exist
    [SerializeField] GameObject      loadPanel;          // the Load-save screen (MenuLoadPanel)

    bool busy;

    void Start() {
        Session.LoadRemembered();
        RefreshPanels();
    }

    // Tab cycles focus between the username and password fields while the login form
    // is up — the standard form-navigation key TMP_InputField doesn't wire by default.
    void Update() {
        if (loginPanel == null || !loginPanel.activeSelf) return;
        if (!Input.GetKeyDown(KeyCode.Tab)) return;
        if (usernameInput != null && usernameInput.isFocused)      FocusField(passwordInput);
        else if (passwordInput != null && passwordInput.isFocused) FocusField(usernameInput);
    }

    void FocusField(TMP_InputField field) {
        if (field == null) return;
        field.Select();
        field.ActivateInputField();
    }

    // Show the login form when logged out, the main menu when logged in. The load
    // screen is a sub-screen of the main menu, hidden whenever we (re)show panels.
    void RefreshPanels() {
        bool loggedIn = Session.LoggedIn;
        if (loginPanel) loginPanel.SetActive(!loggedIn);
        if (mainPanel)  mainPanel.SetActive(loggedIn);
        if (loadPanel)  loadPanel.SetActive(false);
        if (loggedIn && loggedInLabel) loggedInLabel.text = Session.Username;
        // Nothing to continue if there are no saves — New Game generates a fresh world instead.
        if (loggedIn && continueButton) continueButton.interactable = SaveStore.GetSaveSlots().Count > 0;
    }

    // ── Login panel handlers ───────────────────────────────────────────────

    public void OnClickLogin()    { Submit(register: false); }
    public void OnClickRegister() { Submit(register: true); }

    void Submit(bool register) {
        if (busy) return;
        string u = usernameInput ? usernameInput.text.Trim() : "";
        string p = passwordInput ? passwordInput.text : "";
        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p)) { SetStatus("enter username + password"); return; }

        busy = true;
        SetStatus(register ? "registering..." : "logging in...");
        bool remember = rememberToggle == null || rememberToggle.isOn;
        var routine = register
            ? AuthClient.Register(u, p, (ok, name, token, err) => OnAuthDone(ok, name, token, err, remember))
            : AuthClient.Login(u, p, (ok, name, token, err) => OnAuthDone(ok, name, token, err, remember));
        StartCoroutine(routine);
    }

    void OnAuthDone(bool ok, string username, string token, string error, bool remember) {
        busy = false;
        if (!ok) { SetStatus(error); return; }
        Session.SetLogin(username, token, remember);
        SetStatus("");
        RefreshPanels();
    }

    // ── Main menu handlers ─────────────────────────────────────────────────

    // Continue: load the most recent save (WorldController.Start's default — no boot
    // flag needed). Disabled when no saves exist, so this always has something to load.
    public void OnClickContinue() { LoadGame(newGame: false); }
    public void OnClickNewGame()  { LoadGame(newGame: true); }

    // Open the Load-save screen (pick a specific slot instead of the most recent).
    public void OnClickLoad() {
        if (mainPanel) mainPanel.SetActive(false);
        if (loadPanel) loadPanel.SetActive(true);
        if (MenuLoadPanel.instance != null) MenuLoadPanel.instance.Refresh();
        else Debug.LogError("MenuController: MenuLoadPanel.instance is null");
    }

    // Back out of the Load-save screen to the main menu.
    public void OnClickBackFromLoad() {
        if (loadPanel) loadPanel.SetActive(false);
        if (mainPanel) mainPanel.SetActive(true);
    }

    public void OnClickLogout() {
        Session.Logout();
        RefreshPanels();
    }

    public void OnClickQuit() {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    void LoadGame(bool newGame) {
        WorldController.bootNewGame = newGame;
        SceneManager.LoadScene("Main");
    }

    void SetStatus(string s) { if (statusText) statusText.text = s; }
}
