using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Trading panel — shows market bids/offers for a queried item.
//
// Unity setup required:
//   1. Create a Panel "TradingPanel", attach this script, set inactive by default.
//   2. Create a Button "TradingToggle" -> onClick calls TradingPanel.instance.Toggle()
//   3. Inside the panel:
//      - A TMP_InputField "ItemInput" for typing an item name
//      - A Button "QueryButton" -> onClick calls OnClickQuery()
//      - A ScrollRect whose content child has VerticalLayoutGroup + ContentSizeFitter;
//        assign that content child to "ResultList"
//   4. Assign OnlineIndicator (the indicator GameObject with Image + TMP child)

public class TradingPanel : MonoBehaviour {
    public static TradingPanel instance;

    public TMP_InputField itemInput;
    public Transform resultList;
    public GameObject onlineIndicator;

    Image indicatorImage;
    TextMeshProUGUI indicatorText;
    Sprite spriteGreen;
    Sprite spriteRed;

    void Start() {
        if (instance != null) { Debug.LogError("there should only be one TradingPanel"); }
        instance = this;

        spriteGreen = Resources.Load<Sprite>("Sprites/Misc/indicator/green");
        spriteRed   = Resources.Load<Sprite>("Sprites/Misc/indicator/red");

        if (onlineIndicator != null) {
            indicatorImage = onlineIndicator.GetComponentInChildren<Image>();
            indicatorText  = onlineIndicator.GetComponentInChildren<TextMeshProUGUI>();
        }

        if (TradingClient.instance != null) {
            SetIndicator(TradingClient.instance.isOnline);
            TradingClient.instance.OnConnectionChanged += SetIndicator;
            TradingClient.instance.OnMarketResponse    += DisplayMarketData;
        } else {
            SetIndicator(false);
        }
    }

    void OnDestroy() {
        if (TradingClient.instance != null) {
            TradingClient.instance.OnConnectionChanged -= SetIndicator;
            TradingClient.instance.OnMarketResponse    -= DisplayMarketData;
        }
    }

    void SetIndicator(bool online) {
        if (indicatorImage) indicatorImage.sprite = online ? spriteGreen : spriteRed;
        if (indicatorText)  indicatorText.text    = online ? "online" : "offline";
    }

    public void Toggle() {
        gameObject.SetActive(!gameObject.activeSelf);
    }

    public void OnClickQuery() {
        if (itemInput == null) return;
        string item = itemInput.text.Trim();
        if (item.Length == 0) return;
        TradingClient.instance?.QueryMarket(item);
    }

    void DisplayMarketData(MarketBook book) {
        if (resultList == null) return;
        foreach (Transform child in resultList) Destroy(child.gameObject);

        AddRow("<b>--- buys ---</b>", resultList);
        if (book.buys != null)
            foreach (var o in book.buys)
                AddRow($"{o.from}  x{o.quantity}  @ {o.price}", resultList);

        AddRow("<b>--- sells ---</b>", resultList);
        if (book.sells != null)
            foreach (var o in book.sells)
                AddRow($"{o.from}  x{o.quantity}  @ {o.price}", resultList);
    }

    void AddRow(string text, Transform parent) {
        var go  = new GameObject("Row", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text     = text;
        tmp.fontSize = 16;
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 20;
        le.minHeight       = 20;
    }
}
