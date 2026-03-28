using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Sub-view for InfoPanel that displays a single animal's info:
/// name, state, job, inventory, task, stats, and skill widgets.
/// </summary>
public class AnimalInfoView : MonoBehaviour {
    [SerializeField] TextMeshProUGUI text;
    [SerializeField] SkillDisplay    skillDisplayPrefab;
    [SerializeField] Transform       skillsContainer;

    private Animal animal;
    private List<SkillDisplay> _skillDisplays = new List<SkillDisplay>();

    public Animal SelectedAnimal => animal;

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

    // ── Text formatting ────────────────────────────────────────────────────

    static string FormatSlot(Inventory slot) {
        var s = slot.itemStacks[0];
        return s.item != null ? s.item.name + " " + ItemStack.FormatQ(s.quantity, s.item.discrete) : "empty";
    }

    static string FormatAnimal(Animal ani) {
        string t = "animal: " + ani.aName +
            "\n state: " + ani.state.ToString() +
            "\n job: " + ani.job.name +
            "\n [food] " + FormatSlot(ani.foodSlotInv) +
            "\n [tool] " + FormatSlot(ani.toolSlotInv) +
            "\n [top]  " + FormatSlot(ani.clothingSlotInv) +
            "\n inv: " + ani.inv.ToString();
        t += "\n task: " + (ani.task?.ToString() ?? "none");
        t += "\n obj: " + (ani.task?.currentObjective?.ToString() ?? "none");
        if (ani.task is CraftTask craftTask)
            t += "\n recipe: " + craftTask.recipe?.description;
        t += "\n location: " + ani.go.transform.position.ToString() +
            "\n eff: " + ani.efficiency.ToString("F2") +
            "\n full: " + ani.eating.Fullness().ToString("F2") +
            "\n eep: " + ani.eeping.Eepness().ToString("F2") +
            "\n happiness: " + ani.happiness.ToString();
        return t;
    }
}
