#if UNITY_EDITOR
using UnityEditor;

// Dev-only menu to pick which market server the editor connects to. The choice
// is stored in EditorPrefs (per-machine, survives domain reloads) and read by
// TradingClient.ResolveWsUrl on each connect. Shipped builds ignore this and
// always use production.
//
// Tools/Market Server/{Use Local, Use Production}. The active option carries a
// checkmark; default (no pref set) is Local.
static class DevServerMenu {
    const string Key       = MarketServer.EditorPrefUseLocal;
    const string LocalItem = "Tools/Market Server/Use Local (127.0.0.1)";
    const string ProdItem  = "Tools/Market Server/Use Production";

    [MenuItem(LocalItem, false, 0)]
    static void UseLocal() { EditorPrefs.SetBool(Key, true); }

    [MenuItem(LocalItem, true)]
    static bool ValidateLocal() { Menu.SetChecked(LocalItem, EditorPrefs.GetBool(Key, true)); return true; }

    [MenuItem(ProdItem, false, 1)]
    static void UseProd() { EditorPrefs.SetBool(Key, false); }

    [MenuItem(ProdItem, true)]
    static bool ValidateProd() { Menu.SetChecked(ProdItem, !EditorPrefs.GetBool(Key, true)); return true; }
}
#endif
