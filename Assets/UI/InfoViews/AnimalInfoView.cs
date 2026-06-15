using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using TMPro;

// Sub-view for InfoPanel that displays a single animal's info:
// name, state, job, inventory, task, stats, and skill widgets.
public class AnimalInfoView : MonoBehaviour {
    [SerializeField] TextMeshProUGUI text;
    [SerializeField] SkillDisplay    skillDisplayPrefab;
    [SerializeField] Transform       skillsContainer;
    [SerializeField] ComfortBar      tempBar;   // temperature comfort bar (under the temp line)

    private Animal animal;
    private List<SkillDisplay> _skillDisplays = new List<SkillDisplay>();

    public Animal SelectedAnimal => animal;

    // Self-wire the inline-help hover handler onto the text blob (no scene step needed).
    void Awake() {
        if (text != null && text.GetComponent<InfoTextHover>() == null)
            text.gameObject.AddComponent<InfoTextHover>();
    }

    public void Show(Animal animal) {
        this.animal = animal;
        gameObject.SetActive(true);
        RebuildSkillDisplays();
        Refresh();
    }

    public void Hide() {
        gameObject.SetActive(false);
        animal = null;
        ClearSkillDisplays();
    }

    public void Refresh() {
        if (animal == null) return;
        text.text = FormatAnimal(animal);
        // Temp comfort bar: green band = the (clothing/warmth-widened) comfort range,
        // marker = current ambient temperature. Bar follows the view's active state.
        if (tempBar != null) {
            Happiness h = animal.happiness;
            tempBar.Set(h.comfortTempLow, h.comfortTempHigh, WeatherSystem.instance?.temperature);
        }
        foreach (var d in _skillDisplays) d.Refresh();
    }

    // ── Skill widgets ──────────────────────────────────────────────────────

    void RebuildSkillDisplays() {
        ClearSkillDisplays();
        if (skillDisplayPrefab == null || skillsContainer == null) return;
        foreach (Skill sk in System.Enum.GetValues(typeof(Skill))) {
            var widget = Instantiate(skillDisplayPrefab, skillsContainer);
            widget.Setup(sk, animal.skills);
            _skillDisplays.Add(widget);
        }
    }

    void ClearSkillDisplays() {
        foreach (var d in _skillDisplays) Destroy(d.gameObject);
        _skillDisplays.Clear();
    }

    // ── Reachability gating ─────────────────────────────────────────────────
    // This panel hides equip slots and happiness-need lines the player can't yet
    // act on, so the readout only shows what's currently relevant. A gated element
    // appears once EITHER its enabling technology is researched OR the player has
    // ever obtained one of its backing items — so traded-for goods reveal it too
    // (item discovery is permanent; see GlobalInventory.AddItem). Display-only:
    // happiness scoring and the GlobalHappinessPanel listing are unaffected.

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

    static string FormatHappiness(Happiness h) {
        string tempPrefix = h.temperatureScore >= 0 ? "+" : "";
        var sb = new StringBuilder();
        sb.AppendLine($"\nhappiness: {h.score:0.0} / {Db.happinessMaxScore}.0");
        foreach (string need in Db.happinessNeedsSorted) {
            if (!NeedVisible(need)) continue;
            float val = h.GetSatisfaction(need);
            bool sat = val >= Happiness.satisfiedThreshold;
            // Raw satisfaction value is a dev debug aid — Ctrl+D only; players see just o/x.
            string valStr = DebugMode.Enabled ? $"  ({val:0.0})" : "";
            sb.AppendLine($"  {need + ":",-11} {OX(sat)}{valStr}");
        }
        sb.AppendLine($"  {"housing:",-11} {OX(h.house)}");
        sb.AppendLine($"  {"furnishing:",-11} +{h.furnishingScore:0.0}");
        AnimalController ac = AnimalController.instance;
        if (ac != null)
            // Days-of-food detail lives in the GlobalHappinessPanel; keep this line to the bonus.
            sb.AppendLine($"  {"food store:",-11} +{ac.foodStorageHappinessBonus:0.0}");
        sb.Append    ($"  {"temp:",-11} {tempPrefix}{h.temperatureScore:0.0}");
        return sb.ToString();
    }

    static string FormatSlot(Inventory slot) {
        var s = slot.itemStacks[0];
        return s.item != null ? s.item.name + " " + ItemStack.FormatQ(s.quantity, s.item) : "empty";
    }

    static string FormatAnimal(Animal ani) {
        string t = "animal: " + ani.aName +
            "\n state: " + ani.state.ToString() +
            "\n job: " + ani.job.name +
            "\n [food] " + FormatSlot(ani.foodSlotInv);
        // Equip slots stay hidden until the player can fill them (tech or trade).
        if (Reachable("Tools",     Db.equipmentItems))                                              t += "\n [tool] " + FormatSlot(ani.toolSlotInv);
        if (Reachable("Tailoring", Db.clothingItems))                                               t += "\n [top]  " + FormatSlot(ani.clothingSlotInv);
        if (Reachable("Writing",   Db.itemsFlat.Where(i => i != null && i.itemClass == ItemClass.Book))) t += "\n [book] " + FormatSlot(ani.bookSlotInv);
        t += "\n inv: " + ani.inv.ToString();
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
            FormatHappiness(ani.happiness);
        return t;
    }
}
