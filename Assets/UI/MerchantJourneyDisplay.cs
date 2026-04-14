using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Horizontal strip inside TradingPanel showing a head-icon for every merchant
// currently in AnimalState.Traveling. Icons lerp along a line between
// marketAnchor (left) and townAnchor (right) based on the merchant's progress
// through the current TravelingObjective. Clicking an icon opens the animal
// in InfoPanel.
//
// Per-merchant visuals: the icon's Image.sprite and .color are copied from
// the animal's Head child SpriteRenderer at spawn time, so different-coloured
// mice render with their own head on the strip.
//
// Direction detection:
//   HaulToMarketTask   → always outbound (one leg only).
//   HaulFromMarketTask → first leg outbound, second leg returning.
//                        Detected by peeking remaining objectives — if a later
//                        TravelingObjective or ReceiveFromInventoryObjective
//                        is still queued, we haven't reached the market yet.
//   ResumeTravelTask   → direction not persisted; falls back to outbound.
//
// Future: if an "at market" idle state is added, extend the scan to pin
// those animals at marketAnchor.

public class MerchantJourneyDisplay : MonoBehaviour {
    [Header("Strip anchors")]
    public RectTransform marketAnchor;    // left end — the distant city
    public RectTransform townAnchor;      // right end — home
    public RectTransform iconsContainer;  // parent for spawned icons

    [Header("Icon sizing")]
    public Vector2 iconSize = new Vector2(16, 16);

    private readonly Dictionary<Animal, MerchantJourneyIcon> icons = new Dictionary<Animal, MerchantJourneyIcon>();
    private readonly List<Animal> staleBuf = new List<Animal>();
    private readonly HashSet<Animal> activeBuf = new HashSet<Animal>();

    void Update() {
        if (!gameObject.activeInHierarchy) return;
        RefreshIcons();
    }

    void OnDisable() {
        ClearIcons();
    }

    private void RefreshIcons() {
        AnimalController ac = AnimalController.instance;
        if (ac == null) return;
        if (marketAnchor == null || townAnchor == null || iconsContainer == null) {
            Debug.LogError("MerchantJourneyDisplay: missing inspector references");
            return;
        }

        activeBuf.Clear();
        for (int i = 0; i < ac.na; i++) {
            Animal a = ac.animals[i];
            if (a == null) continue;
            if (a.state != Animal.AnimalState.Traveling) continue;
            if (!(a.task?.currentObjective is TravelingObjective obj)) continue;
            activeBuf.Add(a);
            PlaceIcon(a, obj);
        }

        // Remove icons for animals no longer traveling
        staleBuf.Clear();
        foreach (var kvp in icons)
            if (!activeBuf.Contains(kvp.Key)) staleBuf.Add(kvp.Key);
        foreach (Animal a in staleBuf) {
            if (icons[a] != null) Destroy(icons[a].gameObject);
            icons.Remove(a);
        }
    }

    private void PlaceIcon(Animal a, TravelingObjective obj) {
        if (!icons.TryGetValue(a, out MerchantJourneyIcon icon) || icon == null) {
            icon = MerchantJourneyIcon.Create(iconsContainer, iconSize, a);
            icons[a] = icon;
        }

        float t = obj.durationTicks > 0
            ? Mathf.Clamp01(a.workProgress / obj.durationTicks)
            : 0f;
        // Outbound = town → market (right → left). Return = market → town (left → right).
        // Use world-space positions: the two anchor RectTransforms may have different
        // anchorMin/Max, so their anchoredPosition values aren't directly comparable.
        bool outbound = IsOutbound(a);
        Vector3 start = outbound ? townAnchor.position   : marketAnchor.position;
        Vector3 end   = outbound ? marketAnchor.position : townAnchor.position;
        icon.transform.position = Vector3.Lerp(start, end, t);
    }

    private static bool IsOutbound(Animal a) {
        if (a.task is HaulToMarketTask) return true;
        if (a.task is HaulFromMarketTask) {
            foreach (Objective o in a.task.RemainingObjectives())
                if (o is TravelingObjective || o is ReceiveFromInventoryObjective) return true;
            return false;
        }
        return true; // ResumeTravelTask or anything unexpected — default outbound
    }

    private void ClearIcons() {
        foreach (var kvp in icons)
            if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        icons.Clear();
    }
}

// One icon on the journey strip. Built in code via Create(); pulls its sprite
// from the animal's Head child so per-mouse sprite/colour variation shows up
// on the strip, and forwards clicks to InfoPanel via IPointerClickHandler
// (lighter than Button — no transition/interactable machinery we don't use).
public class MerchantJourneyIcon : MonoBehaviour, IPointerClickHandler {
    private Image image;
    private Animal animal;

    public static MerchantJourneyIcon Create(RectTransform parent, Vector2 size, Animal a) {
        GameObject go = new GameObject("MerchantIcon_" + a.aName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.sizeDelta = size;

        Image img = go.AddComponent<Image>();
        img.raycastTarget = true;
        img.preserveAspect = true;

        MerchantJourneyIcon icon = go.AddComponent<MerchantJourneyIcon>();
        icon.image  = img;
        icon.animal = a;
        icon.PopulateSprite();
        return icon;
    }

    private void PopulateSprite() {
        SpriteRenderer head = FindHeadRenderer(animal);
        if (head != null) {
            image.sprite = head.sprite;
            image.color  = head.color;
        } else {
            Debug.LogError($"MerchantJourneyIcon: no Head child found on {animal?.aName}");
        }
    }

    public void OnPointerClick(PointerEventData eventData) {
        if (animal == null) return;
        if (InfoPanel.instance == null) {
            Debug.LogError("MerchantJourneyIcon: no InfoPanel instance");
            return;
        }
        InfoPanel.instance.ShowInfo(new List<Animal> { animal });
    }

    // Recursive — Head lives under Body, not as a direct child of the root.
    // Walking the whole subtree keeps this robust to further hierarchy tweaks.
    private static SpriteRenderer FindHeadRenderer(Animal a) {
        if (a?.go == null) return null;
        foreach (SpriteRenderer sr in a.go.GetComponentsInChildren<SpriteRenderer>(true))
            if (sr.gameObject.name == "Head") return sr;
        return null;
    }
}
