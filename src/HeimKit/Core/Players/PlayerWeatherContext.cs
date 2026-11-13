using System;
using System.Collections.Generic;

namespace HeimKit.Core.Players;

// Passive evidence from WeatherGuard's existing work. No scans, rays or water probes.
internal sealed class PlayerWeatherContext
{
    private sealed class Cell
    {
        internal long Generation;
        internal int Eligible, Managed, RainGates, WaterGates, Confirmed;
        internal float Window = float.NegativeInfinity, LastWear = float.NegativeInfinity;
        internal void WearWindow(float now)
        { if (now < Window || now - Window >= 60) { Window = now; RainGates = WaterGates = Confirmed = 0; } }
    }
    private readonly Dictionary<Vector2s, Cell> _cells = new();
    private readonly HashSet<Vector2s> _centres = new();
    private long _generation, _completed;
    private float _at = float.NegativeInfinity;
    private bool _collect, _limited, _rain, _water;
    private Cell? CellAt(Vector2s zone)
    {
        if (_cells.TryGetValue(zone, out var cell)) return cell;
        if (_cells.Count >= 1024) { _limited = true; return null; }
        _cells.Add(zone, cell = new Cell()); return cell;
    }
    internal void Begin(bool admitted, bool rain, bool water)
    {
        _collect = admitted; if (!admitted) return;
        if (_cells.Count >= 1024) { _cells.Clear(); _completed = 0; _at = float.NegativeInfinity; }
        _generation++; _limited = false; _rain = rain; _water = water;
    }
    internal void Candidate(Vector2s zone, bool managed)
    {
        if (!_collect) return;
        var cell = CellAt(zone); if (cell == null) return;
        if (cell.Generation != _generation) { cell.Generation = _generation; cell.Eligible = cell.Managed = 0; }
        cell.Eligible++; if (managed) cell.Managed++;
    }
    internal void Acquired(Vector2s zone)
    { if (_collect && _cells.TryGetValue(zone, out var cell) && cell.Generation == _generation) cell.Managed++; }
    internal void End(float now, IReadOnlyCollection<Vector2s> centres, bool limited)
    {
        if (!_collect) return;
        _completed = _generation; _at = now; _limited |= limited; _centres.Clear();
        int added = 0; foreach (var centre in centres) { if (added++ >= 8) break; _centres.Add(centre); }
        if (centres.Count > 8) _limited = true;
        _collect = false;
    }
    internal void Gate(float now, Vector2s zone, bool rainWet)
    {
        var cell = CellAt(zone); if (cell == null) return;
        cell.WearWindow(now); cell.LastWear = now;
        if (rainWet) cell.RainGates++; else cell.WaterGates++;
    }
    internal void Confirmed(float now, Vector2s zone)
    {
        var cell = CellAt(zone); if (cell == null) return;
        cell.WearWindow(now); cell.LastWear = now; cell.Confirmed++;
    }
    internal string Read(float now, Vector2s centre, out string key)
    {
        string coverage = float.IsNegativeInfinity(_at) ? "unavailable" : now < _at || now - _at > 3 ? "stale" :
            !_centres.Contains(centre) ? "player_sector_not_sampled" : _limited ? "limited" : "current";
        int eligible = 0, managed = 0, rain = 0, water = 0, confirmed = 0;
        float wearAt = float.NegativeInfinity;
        for (int x = centre.x - 1; x <= centre.x + 1; x++)
        for (int z = centre.y - 1; z <= centre.y + 1; z++)
        {
            if (!_cells.TryGetValue(new Vector2s(x, z), out var cell)) continue;
            if (cell.Generation == _completed) { eligible += cell.Eligible; managed += cell.Managed; }
            if (now >= cell.Window && now - cell.Window < 60)
            { rain += cell.RainGates; water += cell.WaterGates; confirmed += cell.Confirmed; wearAt = Math.Max(wearAt, cell.LastWear); }
        }
        key = coverage + ":" + eligible / 10 + ":" + managed / 10 + ":" + (rain > 0) + ":" + (water > 0) + ":" + (confirmed > 0);
        return "coverage=" + coverage + " scope=3x3_zones last_sample_rain_enabled=" + (float.IsNegativeInfinity(_at) ? "unknown" : _rain.ToString()) +
            " last_sample_water_enabled=" + (float.IsNegativeInfinity(_at) ? "unknown" : _water.ToString()) +
            " eligible=" + (coverage == "current" || coverage == "limited" ? eligible.ToString() : "unknown") +
            " managed=" + (coverage == "current" || coverage == "limited" ? managed.ToString() : "unknown") +
            " native_rain_gates=" + rain + " native_underwater_gates=" + water + " confirmed_preventions=" + confirmed +
            " wear_age_s=" + (float.IsNegativeInfinity(wearAt) ? "unknown" : (now - wearAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)) +
            " wetness_elsewhere=unknown";
    }
    internal void Clear()
    { _cells.Clear(); _centres.Clear(); _generation = _completed = 0; _at = float.NegativeInfinity; _collect = _limited = false; }
}
