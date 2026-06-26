using System.Collections.Generic;
using UnityEngine;

// Horizontal strip inside TradingPanel showing a head-icon for every merchant
// currently in AnimalState.Traveling. Icons lerp along a line between
// marketAnchor (left) and townAnchor (right) based on the merchant's progress
// through the current TravelingObjective. Clicking an icon opens the animal
// in InfoPanel.
//
// Per-merchant visuals: each icon is a shared MouseHeadIcon (the same widget the
// housing occupant lists use), Set() with the animal — so heads render with the
// correct per-mouse fur tint + name/job tooltip by construction, with no logic
// duplicated here.
//
// Direction detection:
//   HaulToMarketTask   → first leg outbound, second leg returning.
//                        Detected by peeking remaining objectives — if a
//                        DeliverToInventoryObjective is still queued, we
//                        haven't reached the market yet.
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

    private readonly Dictionary<Animal, MouseHeadIcon> icons = new Dictionary<Animal, MouseHeadIcon>();
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
        if (!icons.TryGetValue(a, out MouseHeadIcon icon) || icon == null) {
            icon = CreateIcon(a);
            icons[a] = icon;
        }

        float t = obj.durationTicks > 0
            ? Mathf.Clamp01(a.workProgress / obj.durationTicks)
            : 0f;
        // Outbound = town → market (right → left). Return = market → town (left → right).
        // Use world-space positions: the two anchor RectTransforms may have different
        // anchorMin/Max, so their anchoredPosition values aren't directly comparable.
        // NOTE: workProgress ticks in discrete integer steps, so the icon visibly
        // jumps rather than glides. Revisit if smooth inter-tick motion is wanted.
        bool outbound = IsOutbound(a);
        Vector3 start = outbound ? townAnchor.position   : marketAnchor.position;
        Vector3 end   = outbound ? marketAnchor.position : townAnchor.position;
        icon.transform.position = Vector3.Lerp(start, end, t);
    }

    // Builds one strip icon: a sized RectTransform carrying a MouseHeadIcon (its [RequireComponent]
    // Image is added automatically). Clicking opens the mouse in InfoPanel.
    private MouseHeadIcon CreateIcon(Animal a) {
        GameObject go = new GameObject("MerchantIcon_" + a.aName, typeof(RectTransform));
        go.transform.SetParent(iconsContainer, false);
        ((RectTransform)go.transform).sizeDelta = iconSize;

        MouseHeadIcon icon = go.AddComponent<MouseHeadIcon>();
        icon.onClick = m => InfoPanel.instance?.ShowInfo(new List<Animal> { m });
        icon.Set(a);
        return icon;
    }

    private static bool IsOutbound(Animal a) {
        if (a.task is HaulToMarketTask ht) return !ht.IsReturnLeg;
        if (a.task is HaulFromMarketTask hf) return !hf.IsReturnLeg;
        return true; // ResumeTravelTask or anything unexpected — default outbound
    }

    private void ClearIcons() {
        foreach (var kvp in icons)
            if (kvp.Value != null) Destroy(kvp.Value.gameObject);
        icons.Clear();
    }
}
