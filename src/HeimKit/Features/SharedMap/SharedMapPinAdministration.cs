using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Local admin requests use the existing occupied controller; no custom client RPCs or scene loads.
internal sealed class SharedMapPinAdministration : IDisposable
{
    private readonly ZDOMan _world;
    private readonly int _size;
    private readonly ManualLogSource _logger;
    private readonly Func<int> _budget;
    private readonly Action<ZDO> _reconcile;
    private readonly string _request;
    private readonly List<ZDO> _tables = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private int _sector, _cursor, _examined, _invalid;
    private bool _lastChunk, _sweep, _warned;
    private string? _find;
    private readonly ScheduleToken _nextPoll = new();
    public SharedMapPinRemovals Removals { get; }

    public SharedMapPinAdministration(ZDOMan world, int size, string directory, long worldId,
        ManualLogSource logger, Func<int> budget, Action<ZDO> reconcile)
    {
        _world = world; _size = size; _logger = logger; _budget = budget; _reconcile = reconcile;
        Removals = new SharedMapPinRemovals(directory, worldId); Removals.Load();
        _request = Path.Combine(directory, "world-" + worldId.ToString("X16", CultureInfo.InvariantCulture) + ".pin-admin.request");
    }

    public void Tick(float now)
    {
        // Keep a second request pending until the current lookup/sweep finishes.
        if (_find == null && !_sweep && _nextPoll.Ready(now))
        {
            _nextPoll.Postpone(now, 1);
            try { ReadRequest(); _warned = false; }
            catch (Exception error)
            {
                if (!_warned) Write("Could not process pin-administration request: " + error.Message, LogLevel.Warning);
                _warned = true;
            }
        }
        if (_find == null && !_sweep) return;
        try
        {
            if (_cursor >= _tables.Count)
            {
                if (_lastChunk) { Complete(); return; }
                _tables.Clear(); _cursor = 0;
                _lastChunk = _world.GetAllZDOsWithPrefabIterative("piece_cartographytable", _tables, ref _sector);
                if (_tables.Count > 65536) throw new InvalidDataException("Cartography scan candidate limit exceeded.");
            }
            for (int i = 0; i < _budget() && _cursor < _tables.Count; i++)
            {
                var table = _tables[_cursor++];
                if (!table.IsValid() || table.GetPrefab() != SharedMapController.TablePrefab) continue;
                _examined++;
                try
                {
                    var pins = SharedMapTableData.ReadPins(_size, table.GetByteArray(ZDOVars.s_data));
                    if (_sweep) { _reconcile(table); continue; }
                    foreach (var pin in pins)
                    {
                        if (!string.Equals(pin.Name, _find, StringComparison.Ordinal)) continue;
                        string id = SharedMapPinRemovals.Identity(pin);
                        if (!_reported.Add(id)) continue;
                        if (_reported.Count > 64) { Write("Pin lookup stopped after 64 distinct results; additional matches exist."); Complete(); return; }
                        Write("Table pin " + id + ": " + Display(pin.Name) + "; icon " + pin.Icon +
                            "; XYZ " + pin.X.ToString("R", CultureInfo.InvariantCulture) + ", " + pin.Y.ToString("R", CultureInfo.InvariantCulture) +
                            ", " + pin.Z.ToString("R", CultureInfo.InvariantCulture) + "; owner " + pin.Owner.ToString(CultureInfo.InvariantCulture) + ".");
                    }
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException) { _invalid++; }
            }
            if (_lastChunk && _cursor >= _tables.Count) Complete();
        }
        catch (Exception error)
        {
            DiagnosticLog.Write(_logger, "SharedMap", SharedMapLogTopics.Tables, "Pin administration stopped; saved removal rules are retained.", level: LogLevel.Error, error: error);
            Reset();
        }
    }

    private void ReadRequest()
    {
        if (!File.Exists(_request)) return;
        string command;
        using (var stream = new FileStream(_request, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length < 1 || stream.Length > 4096) throw new InvalidDataException("Request must contain one command of at most 4096 bytes.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true)); command = reader.ReadToEnd().Trim();
        }
        int split = command.IndexOf(' ');
        if (split < 1 || command.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new InvalidDataException("Use find <exact label>, remove <pin ID>, or allow <pin ID>.");
        string operation = command.Substring(0, split), argument = command.Substring(split + 1).Trim();
        bool find = operation == "find", remove = operation == "remove", allow = operation == "allow";
        if (argument.Length == 0 || !find && (!remove && !allow || !SharedMapPinRemovals.ValidId(argument)))
            throw new InvalidDataException("Use find <exact label>, remove <pin ID>, or allow <pin ID>.");
        File.Delete(_request); // A request is one-shot even if its later save or scan fails.
        Reset();
        if (find) { _find = argument; Write("Looking up table pins named " + Display(argument) + "."); return; }
        bool changed = Removals.Set(argument, remove); // Persist first; never apply an unsaved rule.
        Write((remove ? "Removal rule enabled for pin " : "Future uploads allowed for pin ") + argument +
            (changed ? "." : " (already configured)."));
        if (remove) _sweep = true;
    }
    private static string Display(string value) => "\"" + new string(value.Take(128).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Replace("\"", "\\\"") + "\"";
    private void Complete()
    {
        Write((_sweep ? "Selected-pin cleanup sweep completed" : "Pin lookup completed with " + Math.Min(64, _reported.Count) + " distinct matches") +
            "; examined " + _examined + " table records" + (_invalid == 0 ? "." : "; skipped " + _invalid + " malformed tables."));
        Reset();
    }
    private void Write(string message, LogLevel level = LogLevel.Info) =>
        DiagnosticLog.Write(_logger, "SharedMap", message, level: level);
    private void Reset() { _tables.Clear(); _reported.Clear(); _find = null; _sweep = _lastChunk = false; _sector = _cursor = _examined = _invalid = 0; }
    public void Dispose() => Reset();
}
