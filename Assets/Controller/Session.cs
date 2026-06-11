using System;
using UnityEngine;

// The logged-in account for this play session: username + auth token, set by the
// menu scene's login flow and read by TradingClient to connect as that identity.
//
// Static (not a MonoBehaviour) so it survives the Menu -> Main scene load without
// a DontDestroyOnLoad object. The token is optionally persisted to PlayerPrefs
// for "remember me"; the in-memory fields are reset at play-session start so a
// stale login never leaks across editor plays (domain reload is disabled — see
// project_plain_csharp_singletons in memory).
public static class Session {
    public static string Username { get; private set; }
    public static string Token    { get; private set; }
    public static bool   LoggedIn => !string.IsNullOrEmpty(Token);

    // Folder/key segment identifying the active account for per-account LOCAL storage
    // (SaveStore.SaveDir, SaveSyncIndex markers). The username, or ".guest" when none —
    // the leading dot can't collide with a real username (the server's usernameRe
    // forbids '.'), so the guest space can never alias an account's folder.
    // Keyed off Username rather than LoggedIn so a mid-game token expiry
    // (ExpireToken) doesn't silently relocate subsequent saves to the guest folder.
    public static string StorageScope => string.IsNullOrEmpty(Username) ? ".guest" : Username;

    const string PrefToken = "session.token";
    const string PrefUser  = "session.username";

    // Clear in-memory state at play start. PlayerPrefs (remembered login) persist;
    // the menu calls LoadRemembered() to restore them deliberately.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() {
        Username = null;
        Token    = null;
    }

    // Restore a remembered, still-valid login (called by the menu at startup).
    public static void LoadRemembered() {
        string t = PlayerPrefs.GetString(PrefToken, "");
        string u = PlayerPrefs.GetString(PrefUser, "");
        if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(u) && IsTokenValid(t)) {
            Token = t;
            Username = u;
        }
    }

    public static void SetLogin(string username, string token, bool remember) {
        Username = username;
        Token = token;
        if (remember) {
            PlayerPrefs.SetString(PrefUser, username);
            PlayerPrefs.SetString(PrefToken, token);
            PlayerPrefs.Save();
        }
    }

    // Username from the remembered login, for pre-filling the login form even after
    // the token itself has lapsed. Empty when nothing is remembered.
    public static string RememberedUsername => PlayerPrefs.GetString(PrefUser, "");

    // True when a login is persisted to PlayerPrefs ("remember me" was on).
    public static bool IsRemembered => !string.IsNullOrEmpty(PlayerPrefs.GetString(PrefToken, ""));

    // The server rejected our token (401 mid-session: TTL lapsed or secret rotated).
    // Drops the token but KEEPS the username, so local storage stays scoped to the
    // account (see StorageScope) and the login form can pre-fill. Distinct from
    // Logout, which is the player explicitly walking away from the account.
    public static void ExpireToken() {
        Token = null;
        PlayerPrefs.DeleteKey(PrefToken);
        PlayerPrefs.Save();
    }

    public static void Logout() {
        Username = null;
        Token = null;
        PlayerPrefs.DeleteKey(PrefToken);
        PlayerPrefs.DeleteKey(PrefUser);
        PlayerPrefs.Save();
    }

    // Token format (see server auth.go): base64url(username) "|" expiryUnix "|" sig.
    // We only read the expiry to avoid connecting with an already-dead token; the
    // signature is authoritative server-side. A malformed token is treated invalid.
    public static bool IsTokenValid(string token) {
        if (string.IsNullOrEmpty(token)) return false;
        string[] parts = token.Split('|');
        if (parts.Length != 3) return false;
        if (!long.TryParse(parts[1], out long expiry)) return false;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry;
    }
}
