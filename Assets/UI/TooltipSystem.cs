using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Reusable tooltip system. Attach to the Canvas (or any persistent parent).
// Call TooltipSystem.Show(title, body) / Hide() from anywhere.
// The tooltip panel is built in code — no prefab needed.
//
// DRAW ORDER: this GameObject must be the LAST sibling under its UI canvas so it renders
// on top of every panel. Stacking is by hierarchy order (Unity's native, canonical way) —
// don't add an override-sorting Canvas, and don't re-front it per-show in code (that would
// fight any other "always on top" element for the top slot). If something ever needs to
// sit above the tooltip, order the two explicitly in the hierarchy.

public class TooltipSystem : MonoBehaviour {
    public static TooltipSystem instance { get; protected set; }

    RectTransform tooltipPanel;
    TextMeshProUGUI titleText;
    TextMeshProUGUI bodyText;
    LayoutElement bodyLe;

    // Body wraps once its natural single-line width would exceed this. Short tooltips still
    // hug their text; only long bodies get held to this width and flow to multiple lines.
    const float MaxBodyWidth = 350f;

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
        bodyText.color     = Color.black;
        bodyText.alignment = TextAlignmentOptions.TopLeft;
        bodyText.raycastTarget = false;
        bodyLe = bodyGo.AddComponent<LayoutElement>();
        var bodyCsf = bodyGo.AddComponent<ContentSizeFitter>();
        bodyCsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        bodyCsf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        // Panel sizing (ContentSizeFitter) is configured on the scene GameObject
        // alongside the VerticalLayoutGroup — no need to add one here.

        panelGo.SetActive(false);
    }

    void Update() {
        if (tooltipPanel == null || !tooltipPanel.gameObject.activeSelf) return;
        UpdatePosition();
    }

    // Position the panel relative to the mouse, flipping sides to keep it on-screen,
    // then snap to integer pixels.
    //
    // Two-stage approach:
    //   1. Default to bottom-right of mouse; flip horizontally / vertically if that
    //      would overflow the right / bottom screen edges.
    //   2. After the flips, clamp to the screen rect — covers the case where the
    //      tooltip is wider than the space on either side of the mouse (a flip-left
    //      from a near-left-edge mouse would otherwise place the panel off-screen).
    //
    // sizeDelta is accurate after ForceRebuildLayoutImmediate (called in Show) or
    // after the first layout pass.
    void UpdatePosition() {
        Vector2 mouse = Input.mousePosition;
        Vector2 pos   = mouse + new Vector2(18f, -18f);

        Vector2 size = tooltipPanel.sizeDelta;
        if (pos.x + size.x > Screen.width)  pos.x = mouse.x - size.x - 14f;
        if (pos.y - size.y < 0)             pos.y = mouse.y + size.y + 14f;

        // Clamp so the panel stays fully on-screen even if the flip overshot the
        // opposite edge. When size > Screen extent the max clamps to 0 / size, so
        // the tooltip's top-left corner stays in view (the right/bottom may still
        // clip, but at least the title is visible).
        pos.x = Mathf.Clamp(pos.x, 0f, Mathf.Max(0f, Screen.width - size.x));
        pos.y = Mathf.Clamp(pos.y, Mathf.Min(size.y, Screen.height), Screen.height);

        tooltipPanel.position = new Vector2(Mathf.Round(pos.x), Mathf.Round(pos.y));
    }

    public static void Show(string title, string body) {
        if (instance == null) return;
        instance.titleText.text        = title;
        instance.bodyText.text         = body;
        // Cap width: measure the body's unconstrained width, then hold it to MaxBodyWidth so
        // long bodies wrap instead of stretching the panel across the screen. Short bodies
        // keep their natural width (and any explicit \n breaks).
        instance.bodyText.enableWordWrapping = true;
        float naturalWidth = instance.bodyText.GetPreferredValues(body, Mathf.Infinity, Mathf.Infinity).x;
        instance.bodyLe.preferredWidth = Mathf.Min(naturalWidth, MaxBodyWidth);
        instance.tooltipPanel.gameObject.SetActive(true);
        // Force TMP to rebuild its mesh first so its preferred-size reports reflect the
        // new text BEFORE we rebuild the layout — otherwise ContentSizeFitter sees the
        // previous frame's text width and we position against stale sizeDelta.
        instance.titleText.ForceMeshUpdate();
        instance.bodyText.ForceMeshUpdate();
        LayoutRebuilder.ForceRebuildLayoutImmediate(instance.tooltipPanel);
        instance.UpdatePosition();
    }

    public static void Hide() {
        if (instance == null) return;
        instance.tooltipPanel.gameObject.SetActive(false);
    }
}
