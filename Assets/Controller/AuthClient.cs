using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// Talks to the market server's auth endpoints (POST /register, /login — see
// server auth.go). Stateless helper: each call is a coroutine the caller (the
// menu) drives via StartCoroutine, reporting the result through a callback.
//
// done(success, username, token, error):
//   success → username + token are set, error is null.
//   failure → username/token null, error is a short player-facing message.
public static class AuthClient {
    [Serializable] class Req  { public string username; public string password; }
    [Serializable] class Resp { public string token; public string username; public string error; }

    public static IEnumerator Register(string username, string password, Action<bool, string, string, string> done)
        => Post("/register", username, password, done);

    public static IEnumerator Login(string username, string password, Action<bool, string, string, string> done)
        => Post("/login", username, password, done);

    // Trade a still-valid token for a fresh full-TTL one (POST /refresh, bearer
    // auth). The menu calls this silently at startup so an active player never
    // re-types a password; failure is the caller's no-op (see MenuController).
    public static IEnumerator Refresh(string token, Action<bool, string, string, string> done) {
        using (var req = new UnityWebRequest(MarketServer.HttpBase + "/refresh", "POST")) {
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", "Bearer " + token);
            yield return req.SendWebRequest();
            Report(req, done);
        }
    }

    static IEnumerator Post(string path, string username, string password, Action<bool, string, string, string> done) {
        string url  = MarketServer.HttpBase + path;
        string body = JsonUtility.ToJson(new Req { username = username, password = password });

        using (var req = new UnityWebRequest(url, "POST")) {
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();
            Report(req, done);
        }
    }

    // The server returns 4xx with a {"error":...} body on failure, which
    // UnityWebRequest flags as ProtocolError — but the body still parses,
    // so try to surface the server's message before a generic fallback.
    static void Report(UnityWebRequest req, Action<bool, string, string, string> done) {
        Resp resp = null;
        string text = req.downloadHandler != null ? req.downloadHandler.text : null;
        if (!string.IsNullOrEmpty(text)) {
            try { resp = JsonUtility.FromJson<Resp>(text); } catch { }
        }

        bool ok = req.result == UnityWebRequest.Result.Success
                  && resp != null && !string.IsNullOrEmpty(resp.token);
        if (ok) {
            done(true, resp.username, resp.token, null);
        } else {
            string err = resp != null && !string.IsNullOrEmpty(resp.error) ? resp.error
                       : req.result != UnityWebRequest.Result.Success ? "can't reach server"
                       : "request failed";
            done(false, null, null, err);
        }
    }
}
