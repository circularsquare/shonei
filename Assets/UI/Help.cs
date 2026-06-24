using System.Collections.Generic;
using UnityEngine;
using TMPro;

// Central registry of help-tooltip copy + the inline-markup helper for in-text "?" icons.
//
// The InfoViews (mouse/building/tile) render a single TMP text blob, so there's no per-stat
// widget to hang a Tooltippable on. Instead each view appends Help.Icon(key) after a stat
// line — that emits the shared help sprite wrapped in a <link>. InfoTextHover detects the
// hovered link and calls Help.TryGet to resolve it back to (title, body) for TooltipSystem.
//
// Panel-header help (Research, Trading, Population, ...) uses scene-authored Tooltippable
// components instead; this registry is only for the inline InfoView case.
public static class Help {
    public struct Entry {
        public string title;
        public string body;
        public Entry(string title, string body) { this.title = title; this.body = body; }
    }

    public const string LinkPrefix = "help:";

    // The sprite asset holding the "help" glyph, loaded once and assigned per-component by
    // InfoTextHover — so only the text blobs that emit help markup reference it, rather than
    // forcing it on as TMP's project-wide default sprite asset.
    const string SpriteAssetResourcePath = "Sprites/misc/HelpIcons";
    static TMP_SpriteAsset _spriteAsset;
    static bool _spriteAssetTried;
    public static TMP_SpriteAsset SpriteAsset {
        get {
            if (!_spriteAssetTried) {
                _spriteAsset = Resources.Load<TMP_SpriteAsset>(SpriteAssetResourcePath);
                _spriteAssetTried = true;
                if (_spriteAsset == null)
                    Debug.LogError("Help: HelpIcons sprite asset missing at Resources/" + SpriteAssetResourcePath);
            }
            return _spriteAsset;
        }
    }

    // key -> copy. Keys are referenced by InfoView markup via Help.Icon("key").
    static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry> {
        { "eff", new Entry("Efficiency",
            "Affects work and move speed. Drops when hungry, sleepy, or cold.") },
        { "condition", new Entry("Condition",
            "Buildings wear down and stop working below 50%. Assign a mender to repair, which " +
            "spends a fraction of the materials used to build it. If a building's top surface is " +
            "exposed to the sky, it will wear down 1.5x as fast.") },
        { "floordecay", new Entry("Floor decay",
            "Items on the ground spoil 5x faster than in storage.") },
        { "wet", new Entry("Wet",
            "Wet items decay twice as fast.") },
        { "sun", new Entry("Sun",
            "Plants grow faster with more open sky overhead.\n" +
            "Solid ground and roofed buildings cast shade. Platforms and greenhouses let light through.") },
        { "foundry", new Entry("Foundry",
            "Fuel builds heat. The hotter the foundry, the faster ore melts.\n" +
            "Below 300 degrees ore will not melt. Melting is fastest at 600 degrees or above.\n" +
            "Melting an item consumes heat.\n" +
            "Heat decays over time, so running continuously is more fuel efficient than repeated cold starts.\n" +
            "The cast target sets which bar to make. Auto makes whatever is most needed, relative to item targets.\n" +
            "Holds up to about 10 bars worth of ore and molten metal at once.") },
    };

    // Transient per-selection copy, set live by an InfoView right before it emits the
    // matching Help.Icon(key) — for tooltips whose text is data-driven (e.g. an
    // extraction building's per-tile yields) rather than static. Checked ahead of the
    // static table in TryGet, so a dynamic entry overrides a static one of the same key.
    static readonly Dictionary<string, Entry> dynamicEntries = new Dictionary<string, Entry>();

    public static void SetDynamic(string key, string title, string body) {
        dynamicEntries[key] = new Entry(title, body);
    }

    // Inline markup appended after a stat line in an InfoView text blob. Renders the shared
    // help sprite inside a <link> so InfoTextHover can detect the hover. Leading space
    // separates it from the preceding text.
    public static string Icon(string key) {
        return " <link=\"" + LinkPrefix + key + "\"><sprite name=\"help\"></link>";
    }

    // Resolves a hovered link id (e.g. "help:eff") to its tooltip copy. Returns false for
    // non-help links or unknown keys so the caller can hide the tooltip.
    public static bool TryGet(string linkId, out string title, out string body) {
        title = null;
        body = null;
        if (string.IsNullOrEmpty(linkId) || !linkId.StartsWith(LinkPrefix)) return false;
        string key = linkId.Substring(LinkPrefix.Length);
        Entry e;
        if (dynamicEntries.TryGetValue(key, out e) || entries.TryGetValue(key, out e)) {
            title = e.title;
            body = e.body;
            return true;
        }
        return false;
    }
}
