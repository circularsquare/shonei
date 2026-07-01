// Implemented by buildings that produce items from a captured SOURCE material rather than
// from their recipe's (deliberately empty) outputs. The craft hook in AnimalStateManager
// routes per-round output, the post-round refresh, and final depletion through this
// interface instead of type-checking each concrete class.
//
// Two implementers today:
//   ExtractionBuilding — gradual solid-tile excavator with the receding "dish" visual.
//     Not wired to any shipping building right now; kept as reusable machinery for a future
//     animated "mine tile" action.
//   WallQuarry — the shipping quarry / digging pit. Dug into an OPEN tile's background wall;
//     the wall's material sets the yield, and depleting it marks the wall quarried-out.
public interface IExtractor {
    // Output distribution for one completed craft round. Returning null signals
    // AnimalStateManager to fall back to the recipe's own outputs.
    ItemQuantity[] GetExtractionOutputs();

    // The captured source's extraction distribution for display (info panel), or null if
    // unavailable. Unlike GetExtractionOutputs this never logs — it's a passive readout.
    ItemQuantity[] CapturedProducts { get; }

    // Called after each NON-final craft round (refresh visuals / worker stand point).
    void OnExtractionRound(Animal animal);

    // Called on FINAL depletion, AFTER the building has been destroyed: consume the source
    // (empty the excavated tile / mark the wall quarried-out). `tile` is the workplace tile.
    void OnExtractionDepleted(Tile tile);
}
