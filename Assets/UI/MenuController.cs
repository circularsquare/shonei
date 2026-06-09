using System.Collections;
using System.Collections.Generic;
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
        // Enable Continue if there's anything to continue: a local save, or (logged in) a
        // possible cloud save — Continue resolves the freshest of the two at click time.
        if (loggedIn && continueButton)
            continueButton.interactable = SaveStore.GetSaveSlots().Count > 0 || Session.LoggedIn;
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

    // Continue: load the freshest save across local + cloud. Compares the newest local
    // file against the newest cloud save (by savedAt — a convenience heuristic; the Load
    // screen's rev-based badges are the precise path). Downloads the cloud copy first if
    // it wins. Offline / logged-out falls straight back to the most-recent local save.
    public void OnClickContinue() {
        if (busy) return;
        busy = true;
        StartCoroutine(ContinueRoutine());
    }
    public void OnClickNewGame()  { LoadGame(newGame: true); }

    IEnumerator ContinueRoutine() {
        string localRecent = SaveStore.GetMostRecentSlot();
        long   localMtime  = localRecent != null ? SaveStore.GetSlotModifiedUnix(localRecent) : 0;

        SaveSync.CloudMeta best = null;
        if (Session.LoggedIn) {
            SetStatus("checking cloud...");
            List<SaveSync.CloudMeta> cloud = null;
            yield return SaveSync.FetchCloudList((ok, list, err) => { if (ok) cloud = list; });
            if (cloud != null)
                foreach (var m in cloud) {
                    if (m.deleted || m.saveVersion > SaveSystem.SaveVersion) continue; // can't load
                    if (best == null || m.savedAt > best.savedAt) best = m;
                }
        }
        SetStatus("");
        busy = false;

        // Cloud wins only if strictly newer than the local recent (or there's no local).
        if (best != null && best.savedAt > localMtime) {
            SetStatus("downloading...");
            yield return DownloadThenBoot(best);
        } else if (localRecent != null) {
            LoadGame(newGame: false); // WorldController.Start loads the most-recent local
        } else {
            LoadGame(newGame: true);  // nothing anywhere → fresh world
        }
    }

    // Materialize a cloud save to disk, record its synced rev, then boot it.
    IEnumerator DownloadThenBoot(SaveSync.CloudMeta meta) {
        bool ok = false; string json = null, error = null;
        yield return SaveSync.Download(meta.slot, (s, j, e) => { ok = s; json = j; error = e; });
        if (!ok) {
            Debug.LogError("MenuController: Continue cloud download failed: " + error);
            SetStatus("");
            // Fall back to whatever is local rather than stranding the player.
            if (SaveStore.GetMostRecentSlot() != null) LoadGame(newGame: false);
            else LoadGame(newGame: true);
            yield break;
        }
        try {
            SaveStore.EnsureDir();
            System.IO.File.WriteAllText(SaveStore.SlotPath(meta.slot), json);
            SaveSyncIndex.Set(meta.slot, meta.savedAt, meta.rev);
            SaveStore.SetAnimalCount(meta.slot, -1);
        } catch (System.Exception e) {
            Debug.LogError("MenuController: failed to materialize cloud save: " + e.Message);
        }
        WorldController.bootSlot = meta.slot;
        SceneManager.LoadScene("Main");
    }

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
