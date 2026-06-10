using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

// Add to a TMP text object that emits Help.Icon(...) markup (the InfoView text blobs).
// Detects hover over the inline help <link> regions and drives TooltipSystem — the same
// surface Tooltippable uses for whole-element tooltips. The text blob has no per-stat
// widgets, so link hit-testing is how we attach a tooltip to an individual line.
[RequireComponent(typeof(TMP_Text))]
public class InfoTextHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler {
    TMP_Text tmp;
    Canvas   canvas;
    bool     isOver;
    int      shownLink = -1; // link index currently showing a tooltip; -1 = none

    void Awake() {
        tmp = GetComponent<TMP_Text>();
        // Required so the pointer enter/exit fire and link hit-testing has a raycast target.
        tmp.raycastTarget = true;
        // Scope the help sprite to just this text (don't rely on TMP's global default sprite asset).
        if (tmp.spriteAsset == null) tmp.spriteAsset = Help.SpriteAsset;
        canvas = GetComponentInParent<Canvas>();
    }

    public void OnPointerEnter(PointerEventData e) { isOver = true; }
    public void OnPointerExit(PointerEventData e)  { isOver = false; HideIfShown(); }

    void Update() {
        if (!isOver) return;
        // Overlay canvases hit-test in screen space (null camera); Camera/World canvases
        // need their render camera.
        Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? canvas.worldCamera : null;
        int link = TMP_TextUtilities.FindIntersectingLink(tmp, Input.mousePosition, cam);
        if (link == shownLink) return; // no change since last frame

        if (link == -1) { HideIfShown(); return; }

        string id = tmp.textInfo.linkInfo[link].GetLinkID();
        string title, body;
        if (Help.TryGet(id, out title, out body)) {
            TooltipSystem.Show(title, body);
            shownLink = link;
        } else {
            HideIfShown();
        }
    }

    void HideIfShown() {
        if (shownLink != -1) {
            TooltipSystem.Hide();
            shownLink = -1;
        }
    }

    void OnDisable() {
        isOver = false;
        HideIfShown();
    }
}
