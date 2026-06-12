using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Centered exclusive panel showing colony-wide happiness breakdown.
// Opened by clicking the happiness HUD element.
//
// Unity setup:
//   headerText    -- TextMeshProUGUI  (population / avg happiness / pop cap / available housing)
//   needContainer -- Transform        (VerticalLayoutGroup -- rows are spawned here)
//   needRowPrefab -- HappinessNeedRow prefab
//   closeButton   -- Button           (the X in the top-right corner; close wired in Awake)
//
//   A "?" InfoButton sits left of the X with a Tooltippable explaining pop cap (scene-authored,
//   parallel to RecipePanel / ResearchPanel).
//
//   Layout: the panel root is a VerticalLayoutGroup that stacks headerText above the scroll
//   view. headerText's LayoutElement.preferredHeight is -1 (content-driven) so the header
//   auto-sizes to its line count; the scroll view's LayoutElement.flexibleHeight=1 absorbs the
//   difference. So adding/removing a header line reflows automatically -- do NOT pin the header
//   height or hand-position the scroll, or that breaks. The X + "?" buttons use
//   LayoutElement.ignoreLayout=true to stay corner-anchored outside the stack.
//
//   HUD: the top-bar readout Button (AnimalController.happinessButton) calls instance.Toggle() in code.
public class GlobalHappinessPanel : MonoBehaviour {
    static GlobalHappinessPanel _instance;
    // Lazily resolves the (possibly inactive) panel so callers can Toggle() it before it has
    // ever been activated. Awake only runs on first activation, so without this the static
    // stays null and the first open silently no-ops. FindObjectOfType(true) includes inactive
    // objects; the result is cached on the backing field.
    public static GlobalHappinessPanel instance {
        get {
            if (_instance == null) _instance = FindObjectOfType<GlobalHappinessPanel>(true);
            return _instance;
        }
        private set { _instance = value; }
    }

    [SerializeField] TextMeshProUGUI  headerText;
    [SerializeField] Transform        needContainer;
    [SerializeField] HappinessNeedRow needRowPrefab;
    [SerializeField] Button           closeButton; // optional X in the corner
    // The "?" InfoButton's Tooltippable. Assigned in the inspector so Refresh can rewrite
    // its body with live colony numbers each tick. Left null → keeps its scene-authored
    // static text (graceful no-op).
    [SerializeField] Tooltippable     populationInfoTip;

    // Rows are spawned once and reused. Order: Db.happinessNeedsSorted, then housing, then temperature.
    readonly List<HappinessNeedRow> rows = new List<HappinessNeedRow>();
    readonly List<string> rowKeys = new List<string>(); // parallel to rows — need key or "housing"/"temperature"

    float refreshTimer;
    const float RefreshInterval = 1f;

    void Awake() {
        // Check the backing field, not the lazy getter (which would resolve to this).
        if (_instance != null && _instance != this) Debug.LogError("Two GlobalHappinessPanels!");
        instance = this;
        UI.RegisterExclusive(gameObject);
        if (closeButton != null) closeButton.onClick.AddListener(() => gameObject.SetActive(false));
    }

    void OnEnable() {
        // SpawnRows on first open rather than Start() — OnEnable fires before Start on first activation,
        // so rows must exist before Refresh() runs or data shows as 0 until the next periodic refresh.
        if (rows.Count == 0) SpawnRows();
        Refresh();
    }

    void Update() {
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

        // Food storage row — colony-wide stat worth up to MaxFoodStorageBonus points.
        // Spawned first so the heaviest single contributor to colony happiness sits at
        // the top of the panel where the player sees it immediately.
        SpawnRow("food storage", AnimalController.MaxFoodStorageBonus);

        // One row per satisfaction need (sorted). All per-need rows are worth 1 happiness
        // point. (Alcohol scores +2 in Happiness.SlowUpdate but shares the same bar width
        // as other needs — the row tracks satisfaction fraction, not the doubled bonus.)
        foreach (string need in Db.happinessNeedsSorted)
            SpawnRow(need, 1f);

        SpawnRow("housing", 1f);
        // Furnishing row — spawned only when there's at least one furnishing in the data,
        // so colonies with no furnishings authored stay visually clean. Sits between housing
        // and temperature since it's house-derived but open-ended.
        if (Db.maxFurnishingPerMouse > 0f)
            SpawnRow("furnishing", Db.maxFurnishingPerMouse);
        SpawnRow("temperature", 2f);
    }

    // Spawns and registers a row, sizing its bar to match the row's happiness-point value
    // in one call so the BarWidthPerPoint × points invariant can't drift over time.
    void SpawnRow(string key, float points) {
        var row = Instantiate(needRowPrefab, needContainer);
        row.Configure(key, points);
        rows.Add(row);
        rowKeys.Add(key);
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
        float sumFurnishing = 0f;

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
            sumFurnishing += h.furnishingScore;
        }

        if (headerText != null) {
            // populationCapacity = happiness-scaled ceiling; totalHousingCapacity = beds.
            // Births are gated by both, so both caps are surfaced as separate lines. The
            // reproduction levers themselves live in the "?" tooltip, not here.
            headerText.text =
                $"{World.instance.SettlementDisplayName} population {n}\n" +
                $"average happiness: {totalScore / n:0.0} points\n" +
                $"happiness population cap: {ac.populationCapacity} mice\n" +
                $"available housing: {ac.totalHousingCapacity}";
        }

        // Live "?" tooltip: state the two reproduction gates with current numbers, not the
        // formula. avgHappiness/populationCapacity come from the same AnimalController stats
        // that drive the header so the cap shown here matches it.
        if (populationInfoTip != null) {
            populationInfoTip.title = "Population";
            populationInfoTip.body =
                "Mice reproduce only with enough housing and happiness.\n" +
                $"happiness {ac.avgHappiness:0.0} supports up to {ac.populationCapacity} mice";
        }

        // Update rows. Every row uses the same Refresh(averagePoints, detailText, tooltip)
        // API; the per-key switch only differs in how it derives those values from the
        // colony aggregates above. Match by rowKeys (parallel to rows) so SpawnRows can
        // omit the furnishing row when no furnishings exist in data.
        for (int i = 0; i < rows.Count; i++) {
            string key = rowKeys[i];
            float avg;
            string detail = "";
            string tooltip;
            switch (key) {
                case "food storage":
                    avg = ac.foodStorageHappinessBonus;
                    tooltip = float.IsInfinity(ac.daysOfFoodInStorage)
                        ? "lots of food in storage"
                        : $"{ac.daysOfFoodInStorage:0.0} days of food in storage";
                    break;
                case "housing":
                    avg = (float)satHousing / n;
                    tooltip = $"{satHousing} of {n} mice have a home";
                    break;
                case "temperature":
                    avg = sumTemp / n;
                    tooltip = $"{avg / 2f * 100f:0}% average temperature comfort";
                    break;
                case "furnishing":
                    avg = sumFurnishing / n;
                    tooltip = $"{avg:0.0} / {Db.maxFurnishingPerMouse:0.0} furnishing points";
                    break;
                default:
                    // Value-based need (wheat, social, etc). Each satisfied mouse contributes
                    // 1 point, so avg points = fraction satisfied. detail shows raw satisfaction
                    // avg as a debug aid (visible distance from the 1.0 satisfied threshold).
                    avg = (float)satCounts[key] / n;
                    float rawAvg = satSums[key] / n;
                    detail = rawAvg.ToString("0.0");
                    tooltip = $"{satCounts[key]} of {n} mice satisfied";
                    break;
            }
            rows[i].Refresh(avg, detail, tooltip);
        }
    }
}
