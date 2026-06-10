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

    void OnDestroy() {
        SaveSync.OnCloudListChanged -= RefreshContinueButton;
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

        RefreshContinueButton();
        // Warm the cloud listing as soon as the menu shows so the Continue button can be
        // gated on real knowledge (not just "logged in"), and the Load screen opens instantly.
        if (loggedIn) {
            SaveSync.OnCloudListChanged -= RefreshContinueButton;
            SaveSync.OnCloudListChanged += RefreshContinueButton;
            StartCoroutine(SaveSync.WarmCloudList());
        }
    }

    // Continue is clickable only when we KNOW there's something to load: a local save, or a
    // confirmed cloud save. While the cloud list is still loading (and there's no local save)
    // it stays disabled — a click must never fall through to a silent new world (the cloud
    // fetch could simply have failed). Re-runs whenever the cached cloud state changes.
    void RefreshContinueButton() {
        if (continueButton == null) return;
        bool hasLocal = SaveStore.GetSaveSlots().Count > 0;
        bool hasCloud = SaveSync.CloudState == SaveSync.CloudListState.Ready
                        && SaveSync.CachedCloud != null
                        && SaveSync.CachedCloud.Exists(IsLoadableCloud);
        continueButton.interactable = hasLocal || hasCloud;
    }

    // A cloud save we can actually load now: not tombstoned, and not authored by a newer
    // game version than this build understands.
    static bool IsLoadableCloud(SaveSync.CloudMeta m) =>
        m != null && !m.deleted && m.saveVersion <= SaveSystem.SaveVersion;

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

        // Make sure the cloud listing is in hand. The menu prefetches it on show, so this is
        // usually a no-op; if a fetch is still running, WarmCloudList waits for it.
        if (Session.LoggedIn && SaveSync.CloudState != SaveSync.CloudListState.Ready
                             && SaveSync.CloudState != SaveSync.CloudListState.Failed) {
            SetStatus("checking cloud...");
            yield return SaveSync.WarmCloudList();
        }
        SetStatus("");
        busy = false;

        // Freshest loadable cloud save (by savedAt), from the cached listing.
        SaveSync.CloudMeta best = null;
        if (SaveSync.CloudState == SaveSync.CloudListState.Ready && SaveSync.CachedCloud != null)
            foreach (var m in SaveSync.CachedCloud) {
                if (!IsLoadableCloud(m)) continue;
                if (best == null || m.savedAt > best.savedAt) best = m;
            }

        // Cloud wins only if strictly newer than the local recent (or there's no local).
        if (best != null && best.savedAt > localMtime) {
            SetStatus("downloading...");
            yield return DownloadThenBoot(best);
        } else if (localRecent != null) {
            LoadGame(newGame: false); // WorldController.Start loads the most-recent local
        } else if (Session.LoggedIn && SaveSync.CloudState == SaveSync.CloudListState.Failed) {
            // No local save AND we couldn't reach the cloud — DON'T silently start a new
            // world (the player's cloud save may well exist, we just can't see it). Keep them
            // on the menu so they can retry. The button is normally disabled in this state;
            // this guards the race where the fetch fails mid-click.
            SetStatus("couldn't reach cloud - try again");
        } else {
            LoadGame(newGame: true);  // genuinely nothing anywhere → fresh world
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
