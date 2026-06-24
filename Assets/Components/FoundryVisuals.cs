using UnityEngine;

// Renders the contents sitting inside a Foundry: the ore chunk(s) melting in the hearth and the cast
// bar resting on the output dish. Both draw as small child sprites positioned at authored pixel spots
// on the 32×32 foundry sprite, using the items' quarter (carry-stack) art (qlow/qmid/qhigh).
//
// The ore sprite sorts BELOW the decorative molten zone (WaterController, sortingOrder 20) so the
// rising molten visibly covers it; the bar sits on the front dish, outside the molten, in front of
// the body. Refresh() is driven from Foundry.Tick (callback, not per-frame polling) — it only hits
// Resources.Load when the displayed item actually changes.
//
// Spawned + Init'd by the Foundry constructor. Child GOs are torn down automatically with the parent.
public class FoundryVisuals : MonoBehaviour {
    // Authored anchor points: the BOTTOM-CENTRE of each item sprite, in pixels from the top-left of
    // the 32×32 foundry sprite. Converted to transform-local (sprite centre = pixel 16,16) at runtime.
    static readonly Vector2 OrePixel = new Vector2(12f, 27f);
    static readonly Vector2 BarPixel = new Vector2(26f, 30f);
    const float SpritePx  = 32f;  // foundry sprite size (2×2 tiles @ 16 px)

    Foundry foundry;
    SpriteRenderer oreSR;
    SpriteRenderer barSR;
    Item lastOre;   // cache so Refresh only reloads sprites on change
    Item lastBar;

    public void Init(Foundry foundry) {
        this.foundry = foundry;
        int baseOrder = foundry.sr != null ? foundry.sr.sortingOrder : 10;
        // Ore behind the molten zone (pinned at 20) so liquid draws over it; clamp guards against an
        // unusually high building order. Bar just above the body (it's on the dish, outside the molten).
        oreSR = MakeChild("foundry_ore", Mathf.Min(baseOrder + 1, 19));
        barSR = MakeChild("foundry_bar", baseOrder + 1);
        Refresh();
    }

    SpriteRenderer MakeChild(string name, int sortingOrder) {
        GameObject child = new GameObject(name);
        child.transform.SetParent(transform, false);
        SpriteRenderer sr = SpriteMaterialUtil.AddSpriteRenderer(child);
        sr.sortingOrder = sortingOrder;
        LightReceiverUtil.SetSortBucket(sr);
        sr.enabled = false; // shown once Refresh finds content
        return sr;
    }

    // Updates the ore + bar sprites from the foundry's current contents. Cheap when nothing changed.
    public void Refresh() {
        if (foundry == null) return;
        ApplyItem(oreSR, foundry.DominantChunkOre(), OrePixel, ref lastOre);
        ApplyItem(barSR, foundry.output != null ? foundry.output.GetMostItem() : null, BarPixel, ref lastBar);
    }

    // Shows `item` as its small quarter-sprite anchored to the authored pixel spot, or hides the
    // renderer when null. Skips Resources.Load while the displayed item is unchanged.
    void ApplyItem(SpriteRenderer sr, Item item, Vector2 pixel, ref Item lastItem) {
        if (item == null) {
            if (sr.enabled) { sr.enabled = false; sr.sprite = null; }
            lastItem = null;
            return;
        }
        if (item == lastItem && sr.enabled) return;
        // qlow (the small carry-stack art) reads better in the hearth than the fuller qmid/qhigh.
        sr.sprite  = LoadQuarter(item, "qlow");
        sr.enabled = sr.sprite != null;
        if (sr.sprite != null) AnchorBottomCentre(sr, pixel);
        lastItem = item;
    }

    static Sprite LoadQuarter(Item item, string variant) {
        string n = item.name.Trim().Replace(" ", "");
        Sprite s = Resources.Load<Sprite>($"Sprites/Items/split/{n}/{variant}");
        s ??= Resources.Load<Sprite>($"Sprites/Items/split/{n}/qmid");
        s ??= Resources.Load<Sprite>($"Sprites/Items/split/default/{variant}");
        s ??= Resources.Load<Sprite>("Sprites/Items/split/default/qmid");
        return s;
    }

    // Places the sprite so its visible bottom-centre lands at the authored pixel (measured from the
    // sprite's top-left; sprite centre = pixel 16,16). Uses sprite.bounds so it's robust to the
    // quarter art's actual size/pivot — no per-frame offset math, computed once per sprite change.
    void AnchorBottomCentre(SpriteRenderer sr, Vector2 pixel) {
        float half = SpritePx * 0.5f;
        Vector3 target = new Vector3((pixel.x - half) / 16f, (half - pixel.y) / 16f, 0f);
        Bounds b = sr.sprite.bounds; // local-space, relative to pivot
        sr.transform.localPosition = new Vector3(target.x - b.center.x, target.y - b.min.y, 0f);
    }
}
