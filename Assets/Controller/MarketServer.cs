using UnityEngine;

// Single source of truth for the market server's address, used by both the
// WebSocket client (TradingClient) and the HTTP auth client (AuthClient).
//
// Production is always used in shipped builds. In the editor the target is
// dev-selectable via Tools/Market Server (backed by EditorPrefs), defaulting to
// local so ordinary local testing needs no toggle. WS uses ws/wss; the auth
// endpoints are plain HTTP behind the same Caddy TLS front door, so http/https.
public static class MarketServer {
    const string ProdHost  = "market.anita.garden";  // Hetzner, behind Caddy TLS
    const string LocalHost = "127.0.0.1:8083";        // a server running on this machine

#if UNITY_EDITOR
    // Shared with DevServerMenu.cs. true = local, false = production.
    public const string EditorPrefUseLocal = "shonei.market.useLocalServer";
    public static bool UseLocal => UnityEditor.EditorPrefs.GetBool(EditorPrefUseLocal, true);
#else
    public static bool UseLocal => false;
#endif

    public static string WsBase   => UseLocal ? "ws://"   + LocalHost : "wss://"  + ProdHost;
    public static string HttpBase => UseLocal ? "http://" + LocalHost : "https://" + ProdHost;
}
