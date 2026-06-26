using UnityEngine;

// Greenhouse — a climate frame plants grow inside (see SPEC-systems §Plant Growth, greenhouse).
// Lives at depth 5 ("enclosure") so its glass coexists on a tile with the plant it covers (slot 0)
// and any foreground ladder/rope (slot 2). The growth effects (warmed temperature, faster growth,
// reduced moisture use, height cap) are read off the StructType by Plant; this subclass adds the
// runtime state for the SELF-CONTAINED moisture mode.
//
// Moisture mode is decided once at construction (OnPlaced) and persisted — never recomputed on load
// or when the tile below later changes (mirrors ExtractionBuilding.digDir):
//   • GROUND mode — built on a solid soil tile (dirt/sand/clay): plants inside draw from the soil
//     tile below exactly like a bare crop, so rain keeps it watered. selfMoisture is unused.
//   • SELF-CONTAINED mode — built on anything else (stone, or elevated over air on a platform): the
//     greenhouse holds its own isolated moisture pool, refilled ONLY by farmer watering (no rain,
//     no evaporation). This is what lets the farmable area expand upward.
public class Greenhouse : Building {
    // Decided at OnPlaced from the tile below the anchor; persisted via StructureSaveData. A
    // greenhouse keeps whatever mode it was built in even if the tile below later changes.
    public bool selfContained;

    // Isolated soil-moisture pool (0..MoistureMax), only meaningful in self-contained mode. Drained
    // by a plant's transpiration + stage crossings (at the greenhouse's reduced rate) and refilled
    // by the farmer watering system. Persisted.
    public byte selfMoisture;

    // Starting pool for a fresh self-contained greenhouse — enough runway to get a crop going before
    // the first watering, without being so full that watering never matters.
    private const byte InitialSelfMoisture = 60;

    public Greenhouse(StructType st, int x, int y, bool mirrored = false, int shapeIndex = 0)
        : base(st, x, y, mirrored, shapeIndex: shapeIndex) { }

    public override void OnPlaced() {
        base.OnPlaced();
        // Anchor (x, y) is the greenhouse's bottom tile; the plant inside roots there, so the tile
        // below the anchor is what decides whether real soil is available.
        Tile below = World.instance.GetTileAt(x, y - 1);
        selfContained = below == null || !below.type.isSoil;
        if (selfContained) selfMoisture = InitialSelfMoisture;
    }
}
