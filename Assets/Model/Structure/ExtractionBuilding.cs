using UnityEngine;

// Extraction buildings (quarry, digging pit) produce items from the tile they
// were built on, at a distribution defined by that tile's nExtractionProducts
// in tilesDb.json (1 liang of the base material per craft cycle, plus
// chance-rolled rare finds). The original tile type is captured at placement
// time — before StructController replaces the tile with empty — and drives
// output selection each craft cycle via GetExtractionOutputs.
//
// Their recipes in recipesDb.json deliberately have empty noutputs; the craft
// hook in AnimalStateManager routes output through this class instead.
// Persisted across save/load by tile name (see SaveSystem.GatherStructure /
// RestoreStructure).
//
// The plain base class is the quarry (no behaviour beyond extraction);
// DiggingPit subclasses it to add the receding-dish visuals and the
// dig-direction door wiring.
public class ExtractionBuilding : Building {
    public TileType capturedTile;

    public ExtractionBuilding(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    // Called by StructController.Construct before the underlying tile is emptied.
    // Invalid inputs log an error and leave capturedTile null so the fallback
    // path (recipe.outputs) kicks in — guarantees we never silently produce nothing.
    public void CaptureOriginalTile(TileType t) {
        if (t == null || !t.solid) {
            Debug.LogError($"{GetType().Name}.CaptureOriginalTile: invalid tile at {x},{y} (null or non-solid)");
            return;
        }
        capturedTile = t;
    }

    // Returns the captured tile's extraction distribution, or null if unavailable.
    // A null return signals AnimalStateManager to fall back to recipe.outputs.
    public ItemQuantity[] GetExtractionOutputs() {
        if (capturedTile == null) {
            Debug.LogError($"{GetType().Name} at {x},{y} has no capturedTile — falling back to recipe outputs");
            return null;
        }
        if (capturedTile.extractionProducts == null) {
            Debug.LogError($"{GetType().Name} at {x},{y}: tile '{capturedTile.name}' has no nExtractionProducts defined");
            return null;
        }
        return capturedTile.extractionProducts;
    }
}
