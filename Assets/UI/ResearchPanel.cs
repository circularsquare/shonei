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
//                       Cell Size  = (48, 48)
//                       Spacing    = (10, 10)
//                       Start Axis = Horizontal
//                       Constraint = Flexible
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

    // Historic research bar chart (scene-authored RawImage + BarChartGraph). Diverging
    // chart: scientist gain + passive gain stacked upward (two colours), decay downward.
    // Left null → no chart (graceful no-op). Fed from StatsTracker each refresh.
    [SerializeField] BarChartGraph researchChart;
    const int ResearchChartDays = 15; // bars shown, including the in-progress day

    readonly List<ResearchDisplay> spawnedCards = new List<ResearchDisplay>();
    float refreshTimer = 0f;
    const float RefreshInterval = 0.5f;

    void Awake() {
        if (instance != null) { Debug.LogError("two ResearchPanels!"); }
        instance = this;
        UI.RegisterExclusive(gameObject);
        if (closeButton != null) closeButton.onClick.AddListener(() => gameObject.SetActive(false));
    }

    void Update() {
        refreshTimer += Time.deltaTime;
        if (refreshTimer >= RefreshInterval) {
            refreshTimer = 0f;
            foreach (var card in spawnedCards) card.RefreshProgress();
            UpdateChart();
        }
    }

    void OnEnable() {
        Refresh();
        UpdateChart();
    }

    // Feeds the research chart: scientist + passive gains stacked upward (blue/green),
    // total decay downward (red). Last bar is the in-progress day (drawn live).
    void UpdateChart() {
        if (researchChart == null || StatsTracker.instance == null) return;
        var gained  = StatsTracker.instance.Get("research_gained");
        var passive = StatsTracker.instance.Get("research_passive");
        var decayed = StatsTracker.instance.Get("research_decayed");

        var up = new List<BarChartGraph.Segment>(2);
        if (gained != null)
            up.Add(new BarChartGraph.Segment(
                gained.GetSeries(ResearchChartDays, true),
                BarChartGraph.Blue, BarChartGraph.BlueLive, "scientists"));
        if (passive != null)
            up.Add(new BarChartGraph.Segment(
                passive.GetSeries(ResearchChartDays, true),
                BarChartGraph.Green, BarChartGraph.GreenLive, "passive"));

        BarChartGraph.Segment[] down = decayed != null
            ? new[] { new BarChartGraph.Segment(
                decayed.GetSeries(ResearchChartDays, true),
                BarChartGraph.Red, BarChartGraph.RedLive, "decay") }
            : null;

        researchChart.SetSeries(up.ToArray(), down, ResearchChartDays, lastIsLive: true);
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

    void OnClickToggleStudy(ResearchNodeData node) {
        ResearchSystem.instance?.ToggleStudy(node.id);
        SoundManager.instance?.PlaySFX("research_select");
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
                sb.Append("unlocks: ");
                sb.AppendLine(string.Join(", ", visible));
            }
        }

        float p = rs.GetProgress(node.id);
        bool studied = rs.IsStudied(node.id);

        if (rs.IsUnlocked(node.id))
            sb.Append($"known. ({p:0.0} / {node.cost:0})");
        else if (!rs.PrereqsMet(node))
            sb.Append("prerequisites not met.");
        else
            sb.Append($"progress: {p:0.0} / {node.cost:0}");

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
