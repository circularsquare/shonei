using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

// One mouse in the population panel: head portrait + name + job + activity bar (recent time split
// across working/walking/leisure/idle/sleep). Clicking the row (or the head) selects the mouse,
// driving the panel's detail pane. Built as a reused runtime list by PopulationPanel from a prefab.
//
// Setup() re-binds a row to a (possibly different) mouse — re-baking the head portrait, so the panel
// calls it only when a row's binding actually changes. Refresh() is the cheap per-tick content
// repaint (job can change via the job swapper; activity bar always moves).
//
// A dedicated row rather than reusing OccupantRow (head + name + one button): the multi-column shape
// (job + proportional bar) is genuinely different — see plans/population-panel.md.
//
// The row root needs a raycast-target Image (a transparent background) so OnPointerClick fires
// anywhere on the row.
public class PopulationRow : MonoBehaviour, IPointerClickHandler {
    [SerializeField] MouseHeadIcon headIcon;
    [SerializeField] TextMeshProUGUI nameLabel;
    [SerializeField] TextMeshProUGUI jobLabel;
    [SerializeField] ActivityBar activityBar;

    Animal animal;
    System.Action<Animal> onSelect;

    public Animal Animal => animal;

    // Bind this row to a mouse. Re-bakes the head portrait, so call only when the binding changes.
    public void Setup(Animal a, System.Action<Animal> onSelect) {
        animal = a;
        this.onSelect = onSelect;
        if (headIcon != null) { headIcon.Set(a); headIcon.onClick = _ => Select(); }
        Refresh();
    }

    // Cheap per-tick content repaint. Name/head are stable; job and the activity bar move.
    public void Refresh() {
        if (animal == null) return;
        if (nameLabel != null) { nameLabel.text = animal.aName; nameLabel.color = Color.black; }
        if (jobLabel  != null) { jobLabel.text  = animal.job != null ? animal.job.name : ""; jobLabel.color = Color.black; }
        if (activityBar != null) activityBar.SetFractions(animal.activity);
    }

    public void OnPointerClick(PointerEventData e) { Select(); }

    void Select() { onSelect?.Invoke(animal); }
}
