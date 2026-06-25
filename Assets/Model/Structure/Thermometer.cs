using UnityEngine;

// 1×1 sensor building. No work, power, or reservoir — it just reads out the ambient
// temperature two ways:
//   • the top-bar SeasonTimeDisplay shows the numeric temperature once any thermometer
//     exists, and
//   • the liquid column inside this building rises and falls with the temperature.
// The column is drawn by the shared decorative-water path (Building.TryGetDisplayLiquid +
// WaterController), so it matches every other liquid in the game — same shader, tint, and
// surface shimmer. The {name}_w.png mask (auto-scanned in the Structure ctor, alpha ≥ 128 =
// liquid pixel) marks the tube interior at maximum fill; this class only reports how full to
// draw it, so there is no per-building visual code here.
public class Thermometer : Building {
    // Temperature → fill calibration, in °C. At MinC the liquid fills just the bulb; at MaxC it
    // reaches the top of the tube. The weather system's envelope (~4–28 °C, rarely sub-zero) sits
    // comfortably inside this, so the column reads mid-tube on a normal day with headroom to spare.
    const float MinC = 0f;
    const float MaxC = 40f;

    // Rendered rows at MinC and below — the full-bulb level. WaterController draws this many rows
    // from the zone bottom and shimmers the top one, so 5 reads as "4px of water + a surface row",
    // matching the bulb art. The column never drops below this, even when it's freezing.
    const int BulbRows = 5;

    int _zoneRows = -1; // cached zone height in pixels (from the _w mask); never changes once placed

    public Thermometer(StructType st, int x, int y, bool mirrored = false) : base(st, x, y, mirrored) { }

    // Fill the tube to the current ambient temperature, in whole pixel rows so the surface lands on
    // a clean row. Default tint (alpha 0) → the shader's water blue, matching ponds and tanks;
    // surfaceRow shimmers the meniscus like a pond top.
    public override bool TryGetDisplayLiquid(out float fillFraction, out Color32 tint, out bool surfaceRow) {
        tint       = default;   // alpha 0 → default water blue
        surfaceRow = true;

        int rows = ZoneRows();
        if (rows <= 0) { fillFraction = 0f; return false; }

        var ws = WeatherSystem.instance;
        float temp = ws != null ? ws.temperature : MinC;

        // Map [MinC, MaxC] → [BulbRows, rows]. Clamp01 floors sub-zero at the bulb level.
        float t           = Mathf.Clamp01((temp - MinC) / (MaxC - MinC));
        float desiredRows = Mathf.Lerp(BulbRows, rows, t);
        fillFraction = desiredRows / rows; // WaterController rounds (fraction * rows) back to whole rows
        return fillFraction > 0f;
    }

    // Pixel height of the liquid zone, derived once from the scanned _w mask offsets.
    int ZoneRows() {
        if (_zoneRows >= 0) return _zoneRows;
        var offs = waterPixelOffsets;
        if (offs == null || offs.Count == 0) { _zoneRows = 0; return 0; }
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var o in offs) { if (o.y < minY) minY = o.y; if (o.y > maxY) maxY = o.y; }
        _zoneRows = maxY - minY + 1;
        return _zoneRows;
    }
}
