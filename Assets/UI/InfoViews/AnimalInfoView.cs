using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Sub-view for InfoPanel that displays a single animal's info:
// name, state, job, inventory, task, stats, and skill widgets.
public class AnimalInfoView : MonoBehaviour {
    [SerializeField] TextMeshProUGUI text;
    [SerializeField] MouseHeadIcon   headIcon;   // portrait at top; replaces the old "animal: name" line (hover = name + job)
    [SerializeField] Button          findButton; // centers the camera on this mouse
    [SerializeField] ComfortBar      tempBar;   // temperature comfort bar (under the temp line)
    [SerializeField] Button          rangeButton;  // toggles the work-search-range world overlay
    [SerializeField] Button          populationButton; // opens the population panel with this mouse selected

    [SerializeField] EquipGrid gearGrid;  // RPG gear-box grid (food/hat/book/top/tool); shared widget

    private Animal animal;

    public Animal SelectedAnimal => animal;

    // Self-wire the inline-help hover handler onto the text blob (no scene step needed).
    void Awake() {
        if (text != null && text.GetComponent<InfoTextHover>() == null)
            text.gameObject.AddComponent<InfoTextHover>();
        if (rangeButton != null)
            rangeButton.onClick.AddListener(OnClickRange);
        if (findButton != null)
            findButton.onClick.AddListener(OnClickFind);
        if (populationButton != null)
            populationButton.onClick.AddListener(OnClickPopulation);
    }

    // Opens the population panel focused on this mouse — the deep stats (skills, full
    // equipment, happiness breakdown) live there now, one click from the quick-glance info view.
    void OnClickPopulation() {
        if (animal != null) PopulationPanel.instance?.Open(animal);
    }

    // Shows this mouse's work-search range as a world overlay (dismissed by a world click).
    void OnClickRange() {
        if (animal != null) OverlayController.instance?.ShowSearchRange(animal);
    }

    // Jumps the camera to this mouse once, no follow (mirrors the jobs-panel "look at mouse" jump).
    void OnClickFind() {
        if (animal != null) MouseController.instance?.CenterCameraOn(animal.x, animal.y);
    }

    public void Show(Animal animal) {
        this.animal = animal;
        gameObject.SetActive(true);
        if (headIcon != null) headIcon.Set(animal);
        if (gearGrid != null) gearGrid.Bind(animal);
        Refresh();
    }

    public void Hide() {
        gameObject.SetActive(false);
        animal = null;
    }

    public void Refresh() {
        if (animal == null) return;
        text.text = FormatAnimal(animal);
        if (gearGrid != null) gearGrid.Refresh();
        // Temp comfort bar: green band = the (clothing/warmth-widened) comfort range,
        // marker = current ambient temperature. Bar follows the view's active state.
        if (tempBar != null) {
            Happiness h = animal.happiness;
            tempBar.Set(h.comfortTempLow, h.comfortTempHigh, WeatherSystem.instance?.temperature);
        }
    }

    // Skill widgets moved to the population panel's detail pane (MouseDetailView) — the info
    // view stays a quick glance. See plans/population-panel.md.

    // ── Reachability gating ─────────────────────────────────────────────────
    // This panel hides happiness-need lines the player can't yet act on, so the
    // readout only shows what's currently relevant. A gated need appears once EITHER
    // its enabling technology is researched OR the player has ever obtained one of its
    // backing items — so traded-for goods reveal it too (item discovery is permanent;
    // see GlobalInventory.AddItem). Display-only: happiness scoring and the
    // GlobalHappinessPanel listing are unaffected. (Equip slots are no longer gated —
    // they always show in the gear grid, empty slots drawn as placeholder outlines.)

    // Happiness need -> enabling technology name (researchDb.json), or null when
    // no tech grants it (e.g. dairy/cheese is only reachable by trade). Needs
    // absent from this map are always shown (basic foods, fireplace, social).
    static readonly Dictionary<string, string> needGateTech = new Dictionary<string, string> {
        { "soymilk",  "Soymilk" },
        { "dairy",    null },
        { "fountain", "Pumps" },
        { "bench",    "Architecture" },
        { "reading",  "Writing" },
        { "alcohol",  "Fermentation" },
    };

    // Whether a gated element should be shown. Permissive on the tech check when
    // the research system isn't present (editor/tests) so nothing vanishes there.
    static bool Reachable(string tech, IEnumerable<Item> backingItems) {
        if (tech != null) {
            ResearchSystem rs = ResearchSystem.instance;
            if (rs == null || rs.IsUnlockedByName(tech)) return true;
        }
        InventoryController ic = InventoryController.instance;
        if (ic == null || ic.discoveredItems == null) return false;
        foreach (Item it in backingItems)
            if (it != null && ic.discoveredItems.TryGetValue(it.id, out bool discovered) && discovered)
                return true;
        return false;
    }

    static bool NeedVisible(string need) {
        if (!needGateTech.TryGetValue(need, out string tech)) return true; // ungated
        return Reachable(tech, Db.itemsFlat.Where(i => i != null && i.happinessNeed == need));
    }

    // ── Text formatting ────────────────────────────────────────────────────

    static string OX(bool sat) => sat ? "o" : "x";

    // The blob shows just the happiness summary line + a "?" hover; the per-need breakdown moves
    // into that hover's tooltip (set live each refresh via Help.SetDynamic, like the mining yields
    // in StructureInfoView). Keeps the always-on panel short while the detail stays one hover away.
    static string FormatHappiness(Happiness h) {
        // Panel shows just the score (keeps it on one line); the max moves into the tooltip title.
        Help.SetDynamic("happiness", $"happiness {h.score:0.0} / {Db.happinessMaxScore}.0", BuildHappinessBreakdown(h));
        return $"\nhappiness: {h.score:0.0}" + Help.Icon("happiness");
    }

    // Per-need o/x lines plus housing / furnishing / food store / temp — the tooltip body behind
    // the happiness "?" icon. Same formatting as the old inline block, minus the leading indent.
    static string BuildHappinessBreakdown(Happiness h) {
        string tempPrefix = h.temperatureScore >= 0 ? "+" : "";
        var sb = new StringBuilder();
        foreach (string need in Db.happinessNeedsSorted) {
            if (!NeedVisible(need)) continue;
            float val = h.GetSatisfaction(need);
            bool sat = val >= Happiness.satisfiedThreshold;
            // Raw satisfaction value is a dev debug aid — Ctrl+D only; players see just o/x.
            string valStr = DebugMode.Enabled ? $"  ({val:0.0})" : "";
            sb.AppendLine($"{need + ":",-11} {OX(sat)}{valStr}");
        }
        sb.AppendLine($"{"housing:",-11} {OX(h.house)}");
        sb.AppendLine($"{"furnishing:",-11} +{h.furnishingScore:0.0}");
        AnimalController ac = AnimalController.instance;
        if (ac != null)
            // Days-of-food detail lives in the GlobalHappinessPanel; keep this line to the bonus.
            sb.AppendLine($"{"food store:",-11} +{ac.foodStorageHappinessBonus:0.0}");
        sb.Append    ($"{"temp:",-11} {tempPrefix}{h.temperatureScore:0.0}");
        return sb.ToString();
    }

    static string FormatAnimal(Animal ani) {
        // Name is shown by the head-icon portrait (hover = name + job), so the blob opens at the job.
        // Equipment (food/tool/top/hat/book) is rendered by the gear-box grid, not this text blob.
        string t = "job: " + ani.job.name;
        t += "\n inv: " + ani.inv.ToString();
        // Top priority right now: the category that won the mouse's last decision and its urgency.
        t += "\n urgency: " + ani.topUrgencyLabel + " (" + ani.topUrgencyValue.ToString("F2") + ")";
        // Task/objective/recipe/location are dev internals — debug mode only.
        if (DebugMode.Enabled) {
            t += "\n task: " + (ani.task?.ToString() ?? "none");
            t += "\n obj: " + (ani.task?.currentObjective?.ToString() ?? "none");
            if (ani.task is CraftTask craftTask)
                t += "\n recipe: " + craftTask.recipe?.description;
            t += "\n location: " + ani.go.transform.position.ToString();
        }
        t += "\n eff: " + ani.efficiency.ToString("F2") + Help.Icon("eff") +
            "\n full: " + ani.eating.Fullness().ToString("F2") +
            "\n eep: " + ani.eeping.Eepness().ToString("F2") +
            FormatHappiness(ani.happiness) +
            FormatBuffs(ani.buffs);
        return t;
    }

    // Active tonic buffs, one line each, with time remaining. The whole section is omitted when the
    // mouse has no buffs (so the panel stays clean for the common case).
    static string FormatBuffs(BuffSet buffs) {
        var sb = new StringBuilder();
        bool any = false;
        foreach (var b in buffs.Active()) {
            if (!any) { sb.Append("\n current buffs:"); any = true; }
            float days = World.ticksInDay > 0 ? b.remaining / World.ticksInDay : 0f;
            string left = days >= 1f ? $"{days:0}d" : $"{days:0.0}d";
            sb.Append($"\n  {BuffSet.Label(b.type)} ({left})");
        }
        return sb.ToString();
    }
}
