using UnityEngine;
using TMPro;

/// <summary>
/// Sub-view for InfoPanel that displays a single animal's info:
/// name, state, job, inventory, task, stats, skills.
/// </summary>
public class AnimalInfoView : MonoBehaviour {
    [SerializeField] TextMeshProUGUI text;

    private Animal animal;

    public Animal SelectedAnimal => animal;

    public void Show(Animal animal) {
        this.animal = animal;
        gameObject.SetActive(true);
        Refresh();
    }

    public void Hide() {
        gameObject.SetActive(false);
        animal = null;
    }

    public void Refresh() {
        if (animal == null) return;
        text.text = FormatAnimal(animal);
    }

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
            "\n inv: " + ani.inv.ToString();
        t += "\n task: " + (ani.task?.ToString() ?? "none");
        t += "\n objective: " + (ani.task?.currentObjective?.ToString() ?? "none");
        if (ani.task is CraftTask craftTask)
            t += "\n recipe: " + craftTask.recipe?.description;
        t += "\n location: " + ani.go.transform.position.ToString() +
            "\n efficiency: " + ani.efficiency.ToString("F2") +
            "\n fullness: " + ani.eating.Fullness().ToString("F2") +
            "\n eep: " + ani.eeping.Eepness().ToString("F2") +
            "\n happiness: " + ani.happiness.ToString();
        t += "\n skills:";
        foreach (Skill sk in System.Enum.GetValues(typeof(Skill))) {
            int   lv        = ani.skills.GetLevel(sk);
            float xp        = ani.skills.GetXp(sk);
            float threshold = SkillSet.XpThreshold(lv);
            t += $"\n  {sk.ToString().ToLower()}: lv{lv} ({xp:F1}/{threshold:F0} xp)";
        }
        return t;
    }
}
