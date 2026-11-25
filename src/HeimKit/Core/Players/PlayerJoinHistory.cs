using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using HeimKit.Core.Control;
using HeimKit.Core.Diagnostics;

namespace HeimKit.Core.Players;

internal sealed class PlayerJoinHistory
{
    private readonly string _path;
    private readonly ManualLogSource _log;
    private readonly Dictionary<string, (string Name, long JoinedUtcTicks)> _visits = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _online = new();
    private bool _loaded, _dirty;
    private float _retry;
    internal long Revision { get; private set; }

    internal PlayerJoinHistory(string directory, ManualLogSource log)
    {
        _path = Path.Combine(directory, "heimkit.players");
        _log = log;
    }

    internal void Observe(object session, string? account, string name)
    {
        _online.TryGetValue(session, out string? prior);
        if (ulong.TryParse(account, out _) && account!.All(c => c >= '0' && c <= '9'))
            account = "Steam_" + account;
        if (string.IsNullOrWhiteSpace(account))
            account = prior ?? "session:" + Guid.NewGuid().ToString("N");
        bool joined = prior != account;
        _visits.TryGetValue(account, out var previous);
        if (!joined && (previous.Name == name || name.Length == 0)) return;
        _online[session] = account;
        _visits[account] = (name.Length == 0 ? previous.Name ?? "" : name,
            joined ? DateTime.UtcNow.Ticks : previous.JoinedUtcTicks);
        _dirty = true;
        Revision++;
    }

    internal void Depart(object session) { if (_online.Remove(session)) Revision++; }
    internal void ClearOnline() { _online.Clear(); Revision++; }
    internal bool CanBan(string id) => _visits.ContainsKey(id) && !id.StartsWith("session:", StringComparison.Ordinal);
    internal IEnumerable<ModerationRow> Rows()
    {
        var online = new HashSet<string>(_online.Values, StringComparer.Ordinal);
        foreach (var pair in _visits)
            yield return new ModerationRow(online.Contains(pair.Key) ? RosterGroup.Online : RosterGroup.Previous, pair.Key, pair.Value.Name);
    }

    internal void Flush(float now)
    {
        if ((_loaded && !_dirty) || now < _retry) return;
        try
        {
            if (!_loaded)
            {
                // Read the entire prior file before allowing a replacement.
                if (File.Exists(_path))
                    foreach (var line in File.ReadAllLines(_path))
                    {
                        var fields = line.Split('\t');
                        if (fields.Length != 3 || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long joined)) throw new InvalidDataException("Invalid player history record.");
                        string id = Decode(fields[0]), name = Decode(fields[1]);
                        if (!_visits.TryGetValue(id, out var current)) _visits.Add(id, (name, joined));
                        else if (current.Name.Length == 0) _visits[id] = (name, current.JoinedUtcTicks);
                    }
                _loaded = true;
                Revision++;
            }
            if (!_dirty) return;
            string temporary = _path + ".tmp";
            File.WriteAllLines(temporary, _visits.Select(p => Encode(p.Key) + "\t" + Encode(p.Value.Name) +
                "\t" + p.Value.JoinedUtcTicks.ToString(CultureInfo.InvariantCulture)));
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
            _dirty = false;
        }
        catch (Exception error)
        {
            _retry = now + 60;
            DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control, "Player history storage is unavailable. Retrying in one minute.", level: LogLevel.Warning, error: error);
        }
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
