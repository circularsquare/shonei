using System.Collections.Generic;

// Structured representation of what's at a clicked location.
// Built by MouseController, consumed by InfoPanel to generate tabs.
public class SelectionContext {
    public Tile tile;
    public List<Structure> structures = new List<Structure>();  // non-null structures from tile.structs[0..3], in depth order
    public List<Blueprint> blueprints = new List<Blueprint>();  // non-null blueprints from tile.blueprints[0..3], in depth order
    public List<Animal> animals = new List<Animal>();           // animals at the click position (may be empty)

    // Builds a SelectionContext from a tile and optional animal list.
    // Collects all non-null structures and blueprints from the tile's depth arrays.
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
