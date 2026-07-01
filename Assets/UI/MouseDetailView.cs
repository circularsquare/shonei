using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Right-pane detail for the population panel: the selected mouse's full readout — head portrait,
// name, job, core stats, recent-activity breakdown, equipment, temperature comfort, and skill
// widgets. It has more room than the cramped InfoPanel AnimalInfoView, so it shows everything
// ungated and spaced into labeled sections. Reuses SkillDisplay + ComfortBar so the visuals match
// the info panel.
//
// NOTE: gear is now the shared EquipGrid widget (same as AnimalInfoView). The happiness-breakdown
// text is still duplicated between the two views — factor it out next if it drifts. See
// plans/population-panel.md.
public class MouseDetailView : MonoBehaviour {
    [SerializeField] MouseHeadIcon   headIcon;
    [SerializeField] TextMeshProUGUI nameLabel;
    [SerializeField] TextMeshProUGUI statsText;
    [SerializeField] ComfortBar      tempBar;
    [SerializeField] SkillDisplay    skillDisplayPrefab;
    [SerializeField] Transform       skillsContainer;
    [SerializeField] EquipGrid       gearGrid;   // RPG gear-box grid (shared widget with AnimalInfoView)
    [SerializeField] Button          findButton; // centers the camera on the selected mouse

    Animal animal;
    readonly List<SkillDisplay> skillWidgets = new List<SkillDisplay>();

    void Awake() {
        if (findButton != null) findButton.onClick.AddListener(OnClickFind);
    }

    void OnClickFind() {
        if (animal != null) MouseController.instance?.CenterCameraOn(animal.x, animal.y);
    }

    public void Show(Animal a) {
        if (a == animal) { Refresh(); return; }   // same mouse — just repaint, keep skill widgets
        animal = a;
        if (a == null) { Clear(); return; }
        if (headIcon != null) headIcon.Set(a);
        if (gearGrid != null) gearGrid.Bind(a);
        RebuildSkills();
        Refresh();
    }

    public void Clear() {
        animal = null;
        if (nameLabel != null) nameLabel.text = "";
        if (statsText != null) statsText.text = "";
        ClearSkills();
    }

    public void Refresh() {
        if (animal == null) return;
        if (nameLabel != null) { nameLabel.text = animal.aName; nameLabel.color = Color.black; }
        if (statsText != null) { statsText.text = BuildStats(animal); statsText.color = Color.black; }
        if (tempBar != null) {
            Happiness h = animal.happiness;
            tempBar.Set(h.comfortTempLow, h.comfortTempHigh, WeatherSystem.instance?.temperature);
        }
        foreach (var w in skillWidgets) w.Refresh();
        if (gearGrid != null) gearGrid.Refresh();
    }

    // ── Skill widgets (reused SkillDisplay prefab) ──────────────────────────
    void RebuildSkills() {
        ClearSkills();
        if (skillDisplayPrefab == null || skillsContainer == null) return;
        foreach (Skill sk in System.Enum.GetValues(typeof(Skill))) {
            var w = Instantiate(skillDisplayPrefab, skillsContainer);
            w.Setup(sk, animal.skills);
            skillWidgets.Add(w);
        }
    }
    void ClearSkills() {
        foreach (var w in skillWidgets) if (w != null) Destroy(w.gameObject);
        skillWidgets.Clear();
    }

    // ── Text ────────────────────────────────────────────────────────────────
    // Gear is rendered by the EquipGrid widget (food/tool/top/hat/book boxes), not this text blob.
    static string Pct(ActivityTracker act, ActivityGroup g) => Mathf.RoundToInt(act.Fraction(g) * 100f) + "%";

    static string BuildStats(Animal a) {
        var sb = new StringBuilder();
        sb.AppendLine("job: " + a.job.name);
        sb.AppendLine("eff " + a.efficiency.ToString("F2") + "   full " + a.eating.Fullness().ToString("F2") + "   eep " + a.eeping.Eepness().ToString("F2"));

        sb.AppendLine("");
        sb.AppendLine("time");
        var act = a.activity;
        sb.AppendLine("working " + Pct(act, ActivityGroup.Working) + "   walking " + Pct(act, ActivityGroup.Walking) + "   leisure " + Pct(act, ActivityGroup.Leisure));
        sb.AppendLine("idle " + Pct(act, ActivityGroup.Idle) + "   sleep " + Pct(act, ActivityGroup.Sleep));

        Happiness h = a.happiness;
        string tempPrefix = h.temperatureScore >= 0 ? "+" : "";
        sb.AppendLine("");
        sb.AppendLine("happiness " + h.score.ToString("0.0") + " / " + Db.happinessMaxScore + ".0");
        sb.AppendLine("housing " + OX(h.house) + "   temp " + tempPrefix + h.temperatureScore.ToString("0.0") + "   furnish +" + h.furnishingScore.ToString("0.0"));
        // Per-need o/x, two per line to keep the column short.
        var needs = Db.happinessNeedsSorted;
        for (int i = 0; i < needs.Count; i += 2) {
            string line = needs[i] + " " + OX(h.GetSatisfaction(needs[i]) >= Happiness.satisfiedThreshold);
            if (i + 1 < needs.Count) line += "   " + needs[i + 1] + " " + OX(h.GetSatisfaction(needs[i + 1]) >= Happiness.satisfiedThreshold);
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    static string OX(bool sat) => sat ? "o" : "x";
}
