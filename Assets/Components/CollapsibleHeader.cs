using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

// Reusable header row that collapses/expands the rest of its parent panel.
// Place this on a GameObject that sits at sibling index 0 of a VerticalLayoutGroup;
// Toggle() flips the active state of every later sibling (the "content" of the
// panel) and swaps the dropdown sprite. The "header is sibling 0, content is the
// rest" pattern keeps the wiring simple: no separate content reference, no
// re-parenting of runtime-spawned rows.
//
// Click-to-toggle covers the whole row via IPointerClickHandler — the GameObject
// needs at least one Graphic underneath (the dropdown Image, the TMP label, or
// a transparent row-background Image) so the EventSystem raycasts hit.
// Persistence is owned by SaveSystem: it reads `open` at gather time and calls
// SetOpenSilent at restore time, keyed by `saveKey`.
//
// When content rows are spawned at runtime (ItemDisplay, JobDisplay) the
// spawn site should check `open` and SetActive(false) on the new row if the
// panel is collapsed — otherwise newly-spawned rows would show through a
// collapsed panel.
public class CollapsibleHeader : MonoBehaviour, IPointerClickHandler {
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private Image dropdownImage;
    [SerializeField] private Sprite spriteOpen;
    [SerializeField] private Sprite spriteCollapsed;

    // Stable identifier for save/load (e.g. "inventory", "jobs").
    public string saveKey;

    public bool open = true;

    // Fired after open state changes (Toggle or SetOpenSilent). Controllers subscribe to
    // do panel-specific work like resizing an outer wood-frame container, since the
    // header's "toggle later siblings" sweep doesn't reach ancestors.
    public System.Action<bool> onToggled;

    public void SetTitle(string t){
        if (titleText != null) titleText.text = t;
    }

    // Restore from saved state without firing a user-initiated layout rebuild path.
    public void SetOpenSilent(bool o){
        open = o;
        ApplyOpenState(forceLayoutRebuild: true);
        onToggled?.Invoke(open);
    }

    public void OnPointerClick(PointerEventData e){
        Toggle();
    }

    public void Toggle(){
        open = !open;
        ApplyOpenState(forceLayoutRebuild: true);
        onToggled?.Invoke(open);
    }

    // Show/hide every sibling whose index > our own. We don't touch earlier
    // siblings — if someone puts a header lower in the layout group, only
    // the rows after it collapse.
    void ApplyOpenState(bool forceLayoutRebuild){
        Transform parent = transform.parent;
        if (parent == null) return;
        int self = transform.GetSiblingIndex();
        for (int i = self + 1; i < parent.childCount; i++){
            parent.GetChild(i).gameObject.SetActive(open);
        }
        if (dropdownImage != null){
            if (open && spriteOpen != null) dropdownImage.sprite = spriteOpen;
            else if (!open && spriteCollapsed != null) dropdownImage.sprite = spriteCollapsed;
        }
        if (forceLayoutRebuild){
            Canvas.ForceUpdateCanvases();
            RectTransform rt = parent as RectTransform;
            if (rt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }
    }
}
