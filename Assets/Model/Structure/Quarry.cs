using UnityEngine;

// Quarry extracts stone at a distribution that depends on the specific stone
// tile type it was placed on (limestone/granite/slate/etc.). The original tile
// type is captured at placement time — before StructController replaces the tile
// with empty — and drives output selection each craft cycle via GetExtractionOutputs.
//
// The quarry recipe in recipesDb.json deliberately has empty noutputs; the craft
// hook in AnimalStateManager routes quarry output through this class instead.
// Persisted across save/load by name (see SaveSystem.GatherStructure/RestoreStructure).
public class Quarry : Building {
    public TileType capturedTile;

    public Quarry(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    // Called by StructController.Construct before the underlying tile is emptied.
    // Invalid inputs log an error and leave capturedTile null so the fallback
    // path (recipe.outputs) kicks in — guarantees we never silently produce nothing.
    public void CaptureOriginalTile(TileType t) {
        if (t == null || !t.solid) {
            Debug.LogError($"Quarry.CaptureOriginalTile: invalid tile at {x},{y} (null or non-solid)");
            return;
        }
        capturedTile = t;
    }

    // Returns the tile-specific extraction distribution, or null if unavailable.
    // A null return signals AnimalStateManager to fall back to recipe.outputs.
    public ItemQuantity[] GetExtractionOutputs() {
        if (capturedTile == null) {
            Debug.LogError($"Quarry at {x},{y} has no capturedTile — falling back to recipe outputs");
            return null;
        }
        if (capturedTile.extractionProducts == null) {
            Debug.LogError($"Quarry at {x},{y}: tile '{capturedTile.name}' has no nExtractionProducts defined");
            return null;
        }
        return capturedTile.extractionProducts;
    }
}
