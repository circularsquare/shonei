using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Full-screen research panel — icon grid with hover tooltips.
//
// Unity setup:
//   nodeListContent — Transform        (scroll view Content object)
//                     Add GridLayoutGroup to it with:
//                       Cell Size  = (80, 90)
//                       Spacing    = (6, 6)
//                       Start Axis = Horizontal
//                       Constraint = Flexible
//                     Add ContentSizeFitter with Vertical Fit = Preferred Size
//   cardPrefab      — ResearchDisplay prefab
//
//   ResearchToggle button -> ResearchPanel.instance.Toggle()
//
//   RectTransform for full-screen with margin:
//     Anchor Min=(0,0)  Anchor Max=(1,1)  Left=Right=Top=Bottom=20

public class ResearchPanel : MonoBehaviour {
    public static ResearchPanel instance { get; protected set; }

    [Header("UI Refs")]
    public Transform       nodeListContent;
    public ResearchDisplay cardPrefab;

    [Header("Misc")]
    public Button closeButton; // optional X in the corner

    [Header("Debug")]
    public Button debugUnlockAllButton;

    readonly List<ResearchDisplay> spawnedCards = new List<ResearchDisplay>();
    float refreshTimer = 0f;
    const float RefreshInterval = 0.5f;

    void Awake() {
        if (instance != null) { Debug.LogError("two ResearchPanels!"); }
        instance = this;
        UI.RegisterExclusive(gameObject);
        if (closeButton != null) closeButton.onClick.AddListener(() => gameObject.SetActive(false));
    }

    void Start() {
        if (debugUnlockAllButton != null)
            debugUnlockAllButton.onClick.AddListener(OnClickUnlockAll);
    }

    void Update() {
        refreshTimer += Time.deltaTime;
        if (refreshTimer >= RefreshInterval) {
            refreshTimer = 0f;
            foreach (var card in spawnedCards) card.RefreshProgress();
        }
    }

    void OnEnable() {
        Refresh();
    }

    public void Toggle() {
        if (gameObject.activeSelf) gameObject.SetActive(false);
        else UI.OpenExclusive(gameObject);
    }

    public void Refresh() {
        RebuildNodeList();
    }

    void RebuildNodeList() {
        if (nodeListContent == null) return;
        foreach (Transform child in nodeListContent) Destroy(child.gameObject);
        spawnedCards.Clear();

        var rs = ResearchSystem.instance;
        if (rs == null) return;

        foreach (var node in rs.nodes)
            SpawnCard(node, rs);
    }

    void SpawnCard(ResearchNodeData node, ResearchSystem rs) {
        var card = Instantiate(cardPrefab, nodeListContent, false);
        card.name = "Card_" + node.id;
        card.Setup(node, rs, OnClickToggleStudy);
        spawnedCards.Add(card);
    }

    void OnClickUnlockAll() {
        ResearchSystem.instance?.UnlockAll();
        Refresh();
    }

    void OnClickToggleStudy(ResearchNodeData node) {
        ResearchSystem.instance?.ToggleStudy(node.id);
        Refresh();
    }

    public static string BuildTooltipBody(ResearchNodeData node, ResearchSystem rs) {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(node.description))
            sb.AppendLine(node.description);
        if (node.prereqs != null && node.prereqs.Length > 0) {
            sb.Append("Requires: ");
            sb.AppendLine(string.Join(", ", System.Array.ConvertAll(node.prereqs, id => {
                rs.nodeById.TryGetValue(id, out var n);
                return n?.name ?? id.ToString();
            })));
        }
        if (node.unlocks != null && node.unlocks.Length > 0) {
            // Hide the auto-generated "write the X book" recipe — every tech has one,
            // so listing it adds no information.
            Db.bookRecipeIdByTechId.TryGetValue(node.id, out int bookRecipeId);
            var visible = new System.Collections.Generic.List<string>(node.unlocks.Length);
            foreach (var e in node.unlocks) {
                if (e != null && e.type == "recipe"
                        && int.TryParse(e.target, out int rid) && rid == bookRecipeId)
                    continue;
                visible.Add(FormatUnlock(e));
            }
            if (visible.Count > 0) {
                sb.Append("Unlocks: ");
                sb.AppendLine(string.Join(", ", visible));
            }
        }

        float p = rs.GetProgress(node.id);
        bool studied = rs.IsStudied(node.id);

        if (rs.IsUnlocked(node.id))
            sb.Append($"Known. ({p:0.0} / {node.cost:0})");
        else if (!rs.PrereqsMet(node))
            sb.Append("Prerequisites not met.");
        else
            sb.Append($"Progress: {p:0.0} / {node.cost:0}");

        if (studied)
            sb.Append(" [studying]");

        return sb.ToString().TrimEnd();
    }

    // Resolve an unlock entry to a player-readable label. Recipe ids become recipe descriptions;
    // buildings/misc show the raw target string.
    static string FormatUnlock(UnlockEntry e) {
        if (e == null) return "?";
        if (e.type == "recipe" && int.TryParse(e.target, out int rid)
                && rid >= 0 && rid < Db.recipes.Length && Db.recipes[rid] != null)
            return Db.recipes[rid].description;
        return e.target;
    }
}
