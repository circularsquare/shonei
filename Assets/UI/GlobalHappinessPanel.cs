using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

// Full-screen exclusive panel showing colony-wide happiness breakdown.
// Opened by clicking the happiness HUD element.
//
// Unity setup:
//   headerText    -- TextMeshProUGUI  (colony average + pop capacity)
//   needContainer -- Transform        (VerticalLayoutGroup -- rows are spawned here)
//   needRowPrefab -- HappinessNeedRow prefab
//
//   RectTransform: Anchor Min=(0,0) Max=(1,1), Left/Right/Top/Bottom = 20
//
//   HUD: add Button to AnimalController.happinessPanel, onClick -> GlobalHappinessPanel.instance.Toggle()
public class GlobalHappinessPanel : MonoBehaviour {
    public static GlobalHappinessPanel instance { get; private set; }

    [SerializeField] TextMeshProUGUI  headerText;
    [SerializeField] Transform        needContainer;
    [SerializeField] HappinessNeedRow needRowPrefab;

    // Rows are spawned once and reused. Order: Db.happinessNeedsSorted, then housing, then temperature.
    readonly List<HappinessNeedRow> rows = new List<HappinessNeedRow>();
    readonly List<string> rowKeys = new List<string>(); // parallel to rows — need key or "housing"/"temperature"

    float refreshTimer;
    const float RefreshInterval = 1f;

    void Awake() {
        if (instance != null) Debug.LogError("Two GlobalHappinessPanels!");
        instance = this;
        UI.RegisterExclusive(gameObject);
    }

    void Start() {
        SpawnRows();
    }

    void OnEnable() {
        Refresh();
    }

    void Update() {
        if (gameObject.activeSelf
                && Input.GetMouseButtonDown(0)
                && !EventSystem.current.IsPointerOverGameObject()) {
            gameObject.SetActive(false);
            return;
        }
        refreshTimer += Time.deltaTime;
        if (refreshTimer >= RefreshInterval) {
            refreshTimer = 0f;
            Refresh();
        }
    }

    public void Toggle() {
        if (gameObject.activeSelf) gameObject.SetActive(false);
        else UI.OpenExclusive(gameObject);
    }

    // ── Row setup ─────────────────────────────────────────────────────────

    void SpawnRows() {
        if (needRowPrefab == null || needContainer == null) return;
        foreach (Transform child in needContainer) Destroy(child.gameObject);
        rows.Clear();
        rowKeys.Clear();

        // One row per satisfaction need (sorted), then housing, then temperature
        foreach (string need in Db.happinessNeedsSorted) {
            var row = Instantiate(needRowPrefab, needContainer);
            row.SetNeedName(need);
            rows.Add(row);
            rowKeys.Add(need);
        }
        // Housing row
        var housingRow = Instantiate(needRowPrefab, needContainer);
        housingRow.SetNeedName("housing");
        rows.Add(housingRow);
        rowKeys.Add("housing");
        // Temperature row
        var tempRow = Instantiate(needRowPrefab, needContainer);
        tempRow.SetNeedName("temperature");
        rows.Add(tempRow);
        rowKeys.Add("temperature");
    }

    // ── Refresh ───────────────────────────────────────────────────────────

    void Refresh() {
        refreshTimer = 0f;
        var ac = AnimalController.instance;
        if (ac == null || ac.na == 0) {
            if (headerText != null) headerText.text = "No animals.";
            return;
        }

        int n = ac.na;
        float totalScore = 0f;

        // Accumulate per-need stats
        Dictionary<string, int> satCounts = new Dictionary<string, int>();
        Dictionary<string, float> satSums = new Dictionary<string, float>();
        foreach (string need in Db.happinessNeedsSorted) {
            satCounts[need] = 0;
            satSums[need] = 0f;
        }
        int satHousing = 0;
        float sumTemp = 0f;

        for (int i = 0; i < n; i++) {
            var h = ac.animals[i].happiness;
            totalScore += h.score;
            foreach (string need in Db.happinessNeedsSorted) {
                float val = h.GetSatisfaction(need);
                if (val >= Happiness.satisfiedThreshold) satCounts[need]++;
                satSums[need] += val;
            }
            if (h.house) satHousing++;
            sumTemp += h.temperatureScore;
        }

        if (headerText != null) {
            headerText.text =
                $"Colony Happiness: {totalScore / n:0.0} / {Db.happinessMaxScore}.0   ({n} mice)\n" +
                $"pop capacity: {ac.populationCapacity}";
        }

        // Update rows
        int rowIdx = 0;
        foreach (string need in Db.happinessNeedsSorted) {
            if (rowIdx >= rows.Count) break;
            rows[rowIdx].Refresh(satCounts[need], n, satSums[need] / n);
            rowIdx++;
        }
        // Housing
        if (rowIdx < rows.Count) {
            rows[rowIdx].RefreshBool(satHousing, n);
            rowIdx++;
        }
        // Temperature
        if (rowIdx < rows.Count) {
            rows[rowIdx].RefreshTemp(sumTemp / n);
            rowIdx++;
        }
    }
}
