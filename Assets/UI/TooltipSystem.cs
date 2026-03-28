using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Reusable tooltip system. Attach to the Canvas (or any persistent parent).
// Call TooltipSystem.Show(title, body) / Hide() from anywhere.
// The tooltip panel is built in code — no prefab needed.

public class TooltipSystem : MonoBehaviour {
    public static TooltipSystem instance { get; protected set; }

    RectTransform tooltipPanel;
    TextMeshProUGUI titleText;
    TextMeshProUGUI bodyText;
    LayoutElement bodyLe;

    void Awake() {
        if (instance != null) { Debug.LogError("two TooltipSystems!"); return; }
        instance = this;
        BuildPanel();
    }

    void BuildPanel() {
        var panelGo = gameObject;

        tooltipPanel = panelGo.GetComponent<RectTransform>();
        tooltipPanel.pivot = new Vector2(0f, 1f); // top-left — must match position math below

        // Title
        var titleGo = new GameObject("Title", typeof(RectTransform));
        titleGo.transform.SetParent(panelGo.transform, false);
        titleText           = titleGo.AddComponent<TextMeshProUGUI>();
        titleText.fontSize  = 16;
        titleText.color     = Color.black;
        titleText.alignment = TextAlignmentOptions.TopLeft;
        // titleText.fontStyle = FontStyles.Bold;
        titleText.enableWordWrapping = false;
        titleText.raycastTarget = false;
        var titleCsf = titleGo.AddComponent<ContentSizeFitter>();
        titleCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        titleCsf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        // Body
        var bodyGo = new GameObject("Body", typeof(RectTransform));
        bodyGo.transform.SetParent(panelGo.transform, false);
        bodyText           = bodyGo.AddComponent<TextMeshProUGUI>();
        bodyText.fontSize  = 16;
        bodyText.color     = new Color(0.20f, 0.20f, 0.20f);
        bodyText.alignment = TextAlignmentOptions.TopLeft;
        bodyText.raycastTarget = false;
        bodyLe = bodyGo.AddComponent<LayoutElement>();
        var bodyCsf = bodyGo.AddComponent<ContentSizeFitter>();
        bodyCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        bodyCsf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        var panelCsf = panelGo.AddComponent<ContentSizeFitter>();
        panelCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        panelGo.SetActive(false);
    }

    // Snap text rect heights to integers after ContentSizeFitter runs
    void LateUpdate() {
        SnapHeight(titleText);
        SnapHeight(bodyText);
    }

    static void SnapHeight(TextMeshProUGUI tmp) {
        if (tmp == null) return;
        var r = tmp.rectTransform;
        r.sizeDelta = new Vector2(r.sizeDelta.x, Mathf.Round(r.sizeDelta.y));
    }

    void Update() {
        if (tooltipPanel == null || !tooltipPanel.gameObject.activeSelf) return;
        UpdatePosition();
    }

    // Clamp so the panel doesn't go off-screen, then snap to integer pixels.
    // sizeDelta is accurate after ForceRebuildLayoutImmediate (called in Show) or after
    // the first layout pass.
    void UpdatePosition() {
        Vector2 mouse = Input.mousePosition;
        Vector2 pos   = mouse + new Vector2(18f, -18f);

        Vector2 size = tooltipPanel.sizeDelta;
        if (pos.x + size.x > Screen.width)  pos.x = mouse.x - size.x - 14f;
        if (pos.y - size.y < 0)             pos.y = mouse.y + size.y + 14f;

        tooltipPanel.position = new Vector2(Mathf.Round(pos.x), Mathf.Round(pos.y));
    }

    public static void Show(string title, string body) {
        if (instance == null) return;
        instance.titleText.text        = title;
        instance.bodyText.text         = body;
        instance.bodyLe.preferredWidth = -1f; // auto-size; body text uses explicit \n breaks
        instance.tooltipPanel.gameObject.SetActive(true);
        // Force layout so sizeDelta is current before we position (avoids one-frame snap).
        LayoutRebuilder.ForceRebuildLayoutImmediate(instance.tooltipPanel);
        instance.UpdatePosition();
        // Render on top of everything else in the canvas.
        // instance.transform.SetAsLastSibling();
    }

    public static void Hide() {
        if (instance == null) return;
        instance.tooltipPanel.gameObject.SetActive(false);
    }
}
