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
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HeimKit.Features.HeimMender;

// Worker callbacks never touch configuration or Unity. Only this feature's section is live.
internal sealed class HeimMenderConfiguration : IDisposable
{
    private readonly ConfigFile _config;
    private readonly HeimMenderSettings _settings;
    private readonly ManualLogSource _log;
    private FileSystemWatcher? _watcher;
    private long _changed;
    private DateTime _stamp;
    private readonly ScheduleToken _nextPoll = new();
    private string _lastError = "";
    public HeimMenderConfiguration(ConfigFile config, HeimMenderSettings settings, ManualLogSource log)
    {
        _config = config; _settings = settings; _log = log;
        _stamp = File.GetLastWriteTimeUtc(config.ConfigFilePath);
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(config.ConfigFilePath)!, Path.GetFileName(config.ConfigFilePath));
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
            _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Renamed += Renamed;
            _watcher.Error += (_, __) => Interlocked.Exchange(ref _changed, DateTime.UtcNow.Ticks);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) { _watcher?.Dispose(); _watcher = null; Report("File watcher unavailable; timestamp polling remains active: " + error.Message); }
    }
    private void Changed(object sender, FileSystemEventArgs args) => Interlocked.Exchange(ref _changed, DateTime.UtcNow.Ticks);
    private void Renamed(object sender, RenamedEventArgs args) => Changed(sender, args);
    public void Tick(float now)
    {
        if (!_nextPoll.Ready(now)) return;
        _nextPoll.Postpone(now, 1);
        try
        {
            var stamp = File.GetLastWriteTimeUtc(_config.ConfigFilePath);
            if (stamp != _stamp) { _stamp = stamp; Interlocked.Exchange(ref _changed, DateTime.UtcNow.Ticks); return; }
            long changed = Interlocked.Read(ref _changed);
            if (changed == 0 || DateTime.UtcNow.Ticks - changed < TimeSpan.TicksPerSecond) return;
            if (Interlocked.CompareExchange(ref _changed, 0, changed) != changed) return;
            var info = new FileInfo(_config.ConfigFilePath);
            if (!info.Exists || info.Length > 1024 * 1024) throw new InvalidDataException("Config missing or exceeds 1 MiB.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool section = false;
            foreach (string line in File.ReadAllLines(info.FullName))
            {
                string text = line.Trim();
                if (text.StartsWith("[")) { section = text == "[HeimMender]"; continue; }
                if (!section || text.Length == 0 || text.StartsWith("#")) continue;
                int split = text.IndexOf('=');
                if (split <= 0) throw new InvalidDataException("Malformed HeimMender setting.");
                string key = text.Substring(0, split).Trim();
                if (values.ContainsKey(key)) throw new InvalidDataException("Duplicate setting: " + key);
                values.Add(key, text.Substring(split + 1).Trim());
            }
            var staged = new List<(ConfigEntryBase Entry, object Value)>();
            foreach (var entry in _settings.Entries)
            {
                if (!values.TryGetValue(entry.Definition.Key, out var raw)) continue;
                object value;
                if (entry.SettingType == typeof(bool)) value = bool.Parse(raw);
                else if (entry.SettingType == typeof(int)) value = int.Parse(raw, CultureInfo.InvariantCulture);
                else if (entry.SettingType == typeof(float))
                { float number = float.Parse(raw, CultureInfo.InvariantCulture); if (!PlayerWorldScope.Finite(number)) throw new FormatException("Nonfinite " + entry.Definition.Key); value = number; }
                else value = raw;
                if (entry.Description.AcceptableValues != null && !entry.Description.AcceptableValues.IsValid(value)) throw new FormatException("Out of range: " + entry.Definition.Key);
                if (!Equals(value, entry.BoxedValue)) staged.Add((entry, value));
            }
            bool save = _config.SaveOnConfigSet;
            try { _config.SaveOnConfigSet = false; foreach (var pair in staged) pair.Entry.BoxedValue = pair.Value; }
            finally { _config.SaveOnConfigSet = save; }
            if (staged.Count > 0) DiagnosticLog.Write(_log, "HeimMender", "Reloaded " + staged.Count + " settings.");
            _lastError = "";
        }
        catch (Exception error) { Report("Configuration reload rejected; keeping current settings: " + error.Message); }
    }
    private void Report(string message)
    { if (message == _lastError) return; _lastError = message; DiagnosticLog.Write(_log, "HeimMender", message, level: LogLevel.Warning); }
    public void Dispose() { _watcher?.Dispose(); _watcher = null; }
}
