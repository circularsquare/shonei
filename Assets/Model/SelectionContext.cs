using System.Collections.Generic;

/// <summary>
/// Structured representation of what's at a clicked location.
/// Built by MouseController, consumed by InfoPanel to generate tabs.
/// </summary>
public class SelectionContext {
    public Tile tile;
    /// <summary>Non-null structures from tile.structs[0..3], in depth order.</summary>
    public List<Structure> structures = new List<Structure>();
    /// <summary>Non-null blueprints from tile.blueprints[0..3], in depth order.</summary>
    public List<Blueprint> blueprints = new List<Blueprint>();
    /// <summary>Animals at the click position (may be empty).</summary>
    public List<Animal> animals = new List<Animal>();

    /// <summary>
    /// Builds a SelectionContext from a tile and optional animal list.
    /// Collects all non-null structures and blueprints from the tile's depth arrays.
    /// </summary>
    public static SelectionContext FromTile(Tile tile, List<Animal> animals = null) {
        var ctx = new SelectionContext { tile = tile };
        if (tile != null) {
            for (int d = 0; d < 4; d++) {
                if (tile.structs[d] != null) ctx.structures.Add(tile.structs[d]);
                if (tile.blueprints[d] != null) ctx.blueprints.Add(tile.blueprints[d]);
            }
        }
        if (animals != null) ctx.animals = animals;
        return ctx;
    }
}
