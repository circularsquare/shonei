using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One collapsible workstation group inside the RecipePanel list. Built entirely in
// code by RecipePanel (no prefab) — mirrors how RecipeDisplay builds its rows.
//
// Layout:
//   RecipeGroup_<tile>  (VerticalLayoutGroup + ContentSizeFitter Vertical=Preferred)
//     Header            (HorizontalLayoutGroup, ~20px, clickable Button)
//       [indicator +/-][building icon][name (N)]
//     Cards             (VerticalLayoutGroup + ContentSizeFitter; cards spawn here)
//
// Cards are built lazily the first time the group is expanded, then just toggled
// active/inactive — so a collapsed panel costs ~one header row per workstation
// instead of every recipe card. Expand state is owned by RecipePanel (persisted),
// keyed by the workstation tile string; this component only mirrors it.

public class RecipeGroupDisplay : MonoBehaviour {
    const float HeaderHeight = 20f;
    const float IconSize     = 20f;
    const float IndicatorW   = 14f;

    string                    tile;
    List<Recipe>              recipes;
    RecipeDisplay             cardPrefab;
    RectTransform             cardsContainer;
    TMP_Text                  indicator;
    readonly List<RecipeDisplay> cards = new List<RecipeDisplay>();

    bool expanded;
    bool built; // cards instantiated yet?

    // tileKey persists across saves; st is the resolved StructType (may be null if a
    // recipe's tile isn't a known building — then we show the name with no icon).
    public void Setup(string tileKey, StructType st, List<Recipe> rs,
                      RecipeDisplay prefab, bool startExpanded) {
        tile       = tileKey;
        recipes    = rs;
        cardPrefab = prefab;

        BuildHeader(st);
        BuildContainer();

        expanded = startExpanded;
        if (expanded) BuildCards();
        cardsContainer.gameObject.SetActive(expanded);
        indicator.text = expanded ? "-" : "+";
    }

    // ── Build ──────────────────────────────────────────────────────────

    void BuildHeader(StructType st) {
        var headerGO = new GameObject("Header", typeof(RectTransform));
        headerGO.transform.SetParent(transform, false);

        // Transparent background that catches the click for the whole row.
        var bg = headerGO.AddComponent<Image>();
        bg.color = new Color(1f, 1f, 1f, 0f);
        bg.raycastTarget = true;

        var hlg = headerGO.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing                = 4f;
        hlg.padding                = new RectOffset(2, 2, 0, 0);
        hlg.childAlignment         = TextAnchor.MiddleLeft;
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;

        var le = headerGO.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = HeaderHeight;

        var btn = headerGO.AddComponent<Button>();
        btn.targetGraphic = bg;
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(() => SetExpanded(!expanded, persist: true));

        // Indicator (+/-). Locked width so it can't shrink to a fractional size.
        indicator = MakeText("Indicator", IndicatorW);

        // Building icon — plain Image (ItemIcon is Item-keyed; this is a StructType
        // sprite). Locked square so the pixel-art icon stays crisp at integer width.
        var iconGO = new GameObject("Icon", typeof(RectTransform));
        iconGO.transform.SetParent(headerGO.transform, false);
        var iconImg = iconGO.AddComponent<Image>();
        iconImg.preserveAspect = true;
        iconImg.raycastTarget  = false;
        Sprite spr = st != null ? st.LoadSprite() : null;
        if (spr != null) iconImg.sprite = spr;
        else iconGO.SetActive(false);
        var iconLE = iconGO.AddComponent<LayoutElement>();
        iconLE.minWidth = iconLE.preferredWidth = IconSize;
        iconLE.flexibleWidth = 0f;

        // Name + recipe count, fills the remaining width.
        var label = MakeText("Label", -1f);
        string name = st != null ? st.name : tile;
        label.text = name + " (" + recipes.Count + ")";
    }

    // width<0 => flexible (fills remaining); width>0 => locked to that width.
    TMP_Text MakeText(string goName, float width) {
        var go = new GameObject(goName, typeof(RectTransform));
        go.transform.SetParent(transform.Find("Header"), false);

        var le = go.AddComponent<LayoutElement>();
        if (width < 0f) {
            le.flexibleWidth = 1f;
        } else {
            le.minWidth = le.preferredWidth = width;
            le.flexibleWidth = 0f;
        }

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.raycastTarget = false;
        // Match the panel's font/size/color via the card prefab's descText.
        if (cardPrefab != null && cardPrefab.descText != null) {
            tmp.font     = cardPrefab.descText.font;
            tmp.fontSize = cardPrefab.descText.fontSize;
            tmp.color    = cardPrefab.descText.color;
        }
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Truncate;
        return tmp;
    }

    void BuildContainer() {
        var go = new GameObject("Cards", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        cardsContainer = go.GetComponent<RectTransform>();

        var vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing                = 4f;
        vlg.padding                = new RectOffset(0, 0, 2, 0);
        vlg.childAlignment         = TextAnchor.UpperLeft;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;

        var fitter = go.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
    }

    void BuildCards() {
        built = true;
        foreach (Recipe r in recipes) {
            var card = Instantiate(cardPrefab, cardsContainer, false);
            card.name = "RecipeDisplay_" + r.id;
            card.Setup(r);
            cards.Add(card);
        }
    }

    // ── Toggle / refresh ───────────────────────────────────────────────

    void SetExpanded(bool exp, bool persist) {
        expanded = exp;
        if (exp && !built) BuildCards();
        cardsContainer.gameObject.SetActive(exp);
        indicator.text = exp ? "-" : "+";

        if (persist) RecipePanel.instance?.SetGroupExpanded(tile, exp);

        // Settle the whole list in one frame so nothing "pops".
        var content = RecipePanel.instance?.recipeListContent as RectTransform;
        if (content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(content);
    }

    // Called by RecipePanel's refresh timer; cheap no-op while collapsed.
    public void RefreshVisibleCards() {
        if (!expanded) return;
        foreach (var c in cards) c.Refresh();
    }
}
