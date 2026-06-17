using System.Threading;
using UnityEngine;

// SingleInstanceGuard — keeps one running copy of the game per machine (per OS
// user session). A second launch detects the first via a named system mutex and
// quits itself immediately, before any scene object Awakes — so the duplicate
// never reaches TradingClient and never connects to the market as the same
// account (which would otherwise kick the real instance off; see TradingClient's
// one-session-per-account handling).
//
// Platform notes:
//   - Windows: a named Mutex in the "Local\" namespace is a per-session kernel
//     object — bulletproof, and released automatically when the process exits
//     (even on a crash), so there's no stale-lock cleanup to do.
//   - macOS: the OS launcher already refuses to start a second instance of the
//     same .app bundle on a normal double-click (it just focuses the running
//     one), so this guard is really only load-bearing on Windows. The mutex is
//     still created on Mac as a belt-and-braces backstop for odd launch paths.
//
// Skipped entirely in the editor so play-mode and a standalone build can run side
// by side during development.
public static class SingleInstanceGuard {
    // Held for the whole process lifetime so the lock isn't released early by GC.
    // No name prefix → "Local\" namespace → scoped to this Windows logon session,
    // which is exactly the "don't double-launch for one user" guarantee we want.
    static Mutex instanceMutex;
    const string MutexName = "Shonei.SingleInstance";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Enforce() {
#if !UNITY_EDITOR
        instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew) {
            Debug.LogWarning("[single-instance] Shonei is already running on this machine; quitting this copy.");
            Application.Quit();
        }
#endif
    }
}
