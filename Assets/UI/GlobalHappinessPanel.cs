using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// Full-screen exclusive panel showing colony-wide happiness breakdown.
/// Opened by clicking the happiness HUD element.
///
/// Unity setup:
///   headerText    — TextMeshProUGUI  (colony average + pop capacity)
///   needContainer — Transform        (VerticalLayoutGroup — rows are spawned here)
///   needRowPrefab — HappinessNeedRow prefab
///
///   RectTransform: Anchor Min=(0,0) Max=(1,1), Left/Right/Top/Bottom = 20
///
///   HUD: add Button to AnimalController.happinessPanel, onClick → GlobalHappinessPanel.instance.Toggle()
/// </summary>
public class GlobalHappinessPanel : MonoBehaviour {
    public static GlobalHappinessPanel instance { get; private set; }

    [SerializeField] TextMeshProUGUI  headerText;
    [SerializeField] Transform        needContainer;
    [SerializeField] HappinessNeedRow needRowPrefab;

    // Rows are spawned once and reused — indices match NeedIndex enum.
    readonly List<HappinessNeedRow> rows = new List<HappinessNeedRow>();

    // SYNC: if happiness needs change in Happiness.cs, update NeedIndex, NeedNames, and Refresh() here.
    enum NeedIndex { Wheat, Fruit, Soymilk, Housing, Fountain, Social, Temperature }
    static readonly string[] NeedNames = { "wheat", "fruit", "soymilk", "housing", "fountain", "social", "temperature" };

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
        foreach (string name in NeedNames) {
            var row = Instantiate(needRowPrefab, needContainer);
            row.SetNeedName(name);
            rows.Add(row);
        }
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
        int satWheat = 0, satFruit = 0, satSoymilk = 0;
        int satHousing = 0, satFountain = 0, satSocial = 0;
        float sumWheat = 0f, sumFruit = 0f, sumSoymilk = 0f;
        float sumFountain = 0f, sumSocial = 0f, sumTemp = 0f;

        for (int i = 0; i < n; i++) {
            var h = ac.animals[i].happiness;
            totalScore += h.score;
            if (h.satWheat    >= Happiness.satisfiedThreshold) satWheat++;
            if (h.satFruit    >= Happiness.satisfiedThreshold) satFruit++;
            if (h.satSoymilk  >= Happiness.satisfiedThreshold) satSoymilk++;
            if (h.house)                                        satHousing++;
            if (h.satFountain >= Happiness.satisfiedThreshold) satFountain++;
            if (h.satSocial   >= Happiness.satisfiedThreshold) satSocial++;
            sumWheat    += h.satWheat;
            sumFruit    += h.satFruit;
            sumSoymilk  += h.satSoymilk;
            sumFountain += h.satFountain;
            sumSocial   += h.satSocial;
            sumTemp     += h.temperatureScore;
        }

        if (headerText != null) {
            headerText.text =
                $"Colony Happiness: {totalScore / n:0.0} / 7.0   ({n} mice)\n" +
                $"pop capacity: {ac.populationCapacity}";
        }

        if (rows.Count < NeedNames.Length) return; // prefab not assigned yet
        rows[(int)NeedIndex.Wheat    ].Refresh    (satWheat,    n, sumWheat    / n);
        rows[(int)NeedIndex.Fruit    ].Refresh    (satFruit,    n, sumFruit    / n);
        rows[(int)NeedIndex.Soymilk  ].Refresh    (satSoymilk,  n, sumSoymilk  / n);
        rows[(int)NeedIndex.Housing  ].RefreshBool(satHousing,  n);
        rows[(int)NeedIndex.Fountain ].Refresh    (satFountain, n, sumFountain / n);
        rows[(int)NeedIndex.Social   ].Refresh    (satSocial,   n, sumSocial   / n);
        rows[(int)NeedIndex.Temperature].RefreshTemp(sumTemp / n);
    }
}
