using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Displays online/offline status for the trading server connection.
// Lives on its own GameObject so it works even when TradingPanel is inactive.
// Loads indicator sprites from Resources and subscribes to TradingClient events.
public class OnlineIndicator : MonoBehaviour {
    Image           indicatorImage;
    TextMeshProUGUI indicatorText;
    Sprite          spriteGreen;
    Sprite          spriteRed;

    void Start() {
        spriteGreen = Resources.Load<Sprite>("Sprites/Misc/indicator/green");
        spriteRed   = Resources.Load<Sprite>("Sprites/Misc/indicator/red");

        indicatorImage = GetComponentInChildren<Image>();
        indicatorText  = GetComponentInChildren<TextMeshProUGUI>();

        var client = TradingClient.instance;
        if (client != null) {
            client.OnConnectionChanged += UpdateIndicator;
            UpdateIndicator(client.isOnline);
        } else {
            UpdateIndicator(false);
        }
    }

    void OnDestroy() {
        var client = TradingClient.instance;
        if (client != null)
            client.OnConnectionChanged -= UpdateIndicator;
    }

    void UpdateIndicator(bool online) {
        if (indicatorImage) indicatorImage.sprite = online ? spriteGreen : spriteRed;
        if (indicatorText)  indicatorText.text    = online ? "online" : "offline";
    }
}
