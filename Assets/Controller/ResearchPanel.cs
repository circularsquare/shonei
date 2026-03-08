using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

// Full-screen research panel — icon grid with hover tooltips.
//
// Unity setup:
//   pointsLabel     — TextMeshProUGUI  (research points display)
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
    public static ResearchPanel instance;

    [Header("UI Refs")]
    public TextMeshProUGUI pointsLabel;
    public Transform       nodeListContent;
    public ResearchDisplay cardPrefab;

    void Awake() {
        if (instance != null) { Debug.LogError("two ResearchPanels!"); }
        instance = this;
    }

    void Start() {
    }

    // todo: move to mousecontroller?
    void Update() {
        if (gameObject.activeSelf
                && Input.GetMouseButtonDown(0)
                && !EventSystem.current.IsPointerOverGameObject())
            gameObject.SetActive(false);
    }

    void OnEnable() {
        Refresh();
    }

    public void Toggle() {
        gameObject.SetActive(!gameObject.activeSelf);
    }

    public void Refresh() {
        UpdatePointsLabel();
        RebuildNodeList();
    }

    void UpdatePointsLabel() {
        if (pointsLabel == null) return;
        var rs = ResearchSystem.instance;
        if (rs == null) { pointsLabel.text = "research"; return; }
        pointsLabel.text = $"research points: {rs.AvailablePoints:0.0}";
    }

    void RebuildNodeList() {
        if (nodeListContent == null) return;
        foreach (Transform child in nodeListContent) Destroy(child.gameObject);

        var rs = ResearchSystem.instance;
        if (rs == null) return;

        foreach (var node in rs.nodes)
            SpawnCard(node, rs);
    }

    void SpawnCard(ResearchNodeData node, ResearchSystem rs) {
        var card = Instantiate(cardPrefab, nodeListContent, false);
        card.name = "Card_" + node.id;
        card.Setup(node, rs, OnClickUnlock);
    }

    void OnClickUnlock(ResearchNodeData node) {
        if (ResearchSystem.instance == null) return;
        if (ResearchSystem.instance.Unlock(node))
            Refresh();
    }

    public static string BuildTooltipBody(ResearchNodeData node, ResearchSystem rs) {
        var sb = new StringBuilder();
        sb.AppendLine($"[{node.type}]   {node.cost:0} pts");
        if (!string.IsNullOrEmpty(node.description))
            sb.AppendLine(node.description);
        if (node.prereqs != null && node.prereqs.Length > 0) {
            sb.Append("Requires: ");
            sb.AppendLine(string.Join(", ", System.Array.ConvertAll(node.prereqs, id => {
                rs.nodeById.TryGetValue(id, out var n);
                return n?.name ?? id.ToString();
            })));
        }
        if (!string.IsNullOrEmpty(node.unlocks))
            sb.AppendLine($"Unlocks: {node.unlocks}");
        if (rs.IsUnlocked(node.id))
            sb.Append("Already unlocked.");
        else if (!rs.CanUnlock(node))
            sb.Append($"Need {node.cost:0} pts  (have {rs.AvailablePoints:0.0})");
        else
            sb.Append("Click to unlock.");
        return sb.ToString().TrimEnd();
    }
}
