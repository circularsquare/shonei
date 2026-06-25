using System.Collections.Generic;
using System.Text;
using UnityEngine;
using TMPro;

// Top-bar readout of the in-game date, shown left of the time-speed buttons. The detail
// shown escalates with what the colony has built/researched:
//   • Always — the season ("winter").
//   • + day-of-season once the Timekeeping technology is researched ("winter 2").
//   • + time-of-day while at least one built clock is powered ("winter 2 5pm").
// Pure display: recomputes from the time/weather/research/power systems on an interval.
[RequireComponent(typeof(TextMeshProUGUI))]
public class SeasonTimeDisplay : MonoBehaviour {
    const string TimekeepingTech = "Timekeeping"; // research node that reveals the day count
    const float  RefreshInterval = 0.5f;          // seconds; unscaled (pure display, ticks while paused)

    TextMeshProUGUI _text;
    float           _timer;
    StructType      _clockType;        // cached once Db resolves it; clock type never changes after
    StructType      _thermometerType;  // ditto

    void Awake() {
        _text = GetComponent<TextMeshProUGUI>();
    }

    void Update() {
        _timer += Time.unscaledDeltaTime;
        if (_timer < RefreshInterval) return;
        _timer = 0f;
        Refresh();
    }

    void Refresh() {
        var ws = WeatherSystem.instance;
        if (ws == null) { _text.text = ""; return; } // menu / pre-world

        // Season — lowercase to match the concise UI style.
        var sb = new StringBuilder(ws.GetSeason().ToLowerInvariant());

        // Day-of-season, gated on the Timekeeping technology. Seasons are evenly sized and
        // aligned to the year start, so day-of-year modulo the season length gives the
        // in-season day (1-based).
        var rs = ResearchSystem.instance;
        if (rs != null && rs.IsUnlockedByName(TimekeepingTech)) {
            float seasonLength = World.daysInYear / 4f;
            int dayInSeason = (int)(ws.GetDayOfYear() % seasonLength) + 1;
            sb.Append(' ').Append(dayInSeason);
        }

        // Time-of-day, only while a built clock is powered (the clock is what "tells time").
        if (HasPoweredClock())
            sb.Append(' ').Append(FormatHour(SunController.GetDayPhase() * 24f));

        // Temperature, only once a thermometer is built. Format shared with the info panel.
        if (HasThermometer())
            sb.Append(' ').Append(WeatherSystem.FormatTemp(ws.temperature));

        _text.text = sb.ToString();
    }

    // True if any constructed clock is currently on a powered network.
    bool HasPoweredClock() {
        var sc = StructController.instance;
        var ps = PowerSystem.instance;
        if (sc == null || ps == null) return false;
        StructType clock = ClockType();
        if (clock == null) return false;
        List<Structure> clocks = sc.GetByType(clock);
        if (clocks == null) return false;
        foreach (Structure s in clocks)
            if (s is Building b && ps.IsBuildingPowered(b)) return true;
        return false;
    }

    StructType ClockType() {
        if (_clockType == null && Db.structTypeByName != null)
            Db.structTypeByName.TryGetValue("clock", out _clockType);
        return _clockType;
    }

    // True if any thermometer has been constructed (no power needed — it's a passive gauge).
    bool HasThermometer() {
        var sc = StructController.instance;
        if (sc == null) return false;
        StructType thermometer = ThermometerType();
        if (thermometer == null) return false;
        List<Structure> built = sc.GetByType(thermometer);
        return built != null && built.Count > 0;
    }

    StructType ThermometerType() {
        if (_thermometerType == null && Db.structTypeByName != null)
            Db.structTypeByName.TryGetValue("thermometer", out _thermometerType);
        return _thermometerType;
    }

    // 24h float → 12h clock label, e.g. "5pm" / "12am" / "12pm".
    static string FormatHour(float hour) {
        int h = Mathf.FloorToInt(hour) % 24;
        if (h < 0) h += 24;
        string suffix = h < 12 ? "am" : "pm";
        int h12 = h % 12;
        if (h12 == 0) h12 = 12;
        return h12 + suffix;
    }
}
