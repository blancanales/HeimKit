using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.InfiniteTorches.Models;

namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorchesDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly Func<bool> _debugEnabled;
    private readonly Func<Dictionary<string, long>>? _totals;
    private Dictionary<string, long> _previous = new();
    private float _lastSummary;
    private bool _reported;
    public const int RefillDetailLimit = 4;
    public InfiniteTorchesDiagnostics(ConfigFile config, ManualLogSource logger)
        : this(new InfiniteTorchesSettings(config, logger).DebugLogging, logger) { }
    public InfiniteTorchesDiagnostics(ConfigEntry<bool> debugMode, ManualLogSource logger, Func<Dictionary<string, long>>? totals = null)
        : this(() => debugMode.Value, logger, totals) { }
    public InfiniteTorchesDiagnostics(HeimKit.Configuration.DebugLoggingPolicy policy, ManualLogSource logger)
        : this(() => policy.Enabled, logger) { }
    public InfiniteTorchesDiagnostics(Func<bool> debugEnabled, ManualLogSource logger, Func<Dictionary<string, long>>? totals = null)
    { _logger = logger; _debugEnabled = debugEnabled; ConfigureDiagnostics(debugEnabled, logger, "InfiniteTorches"); _totals = totals; }
    public bool IsDebugEnabled => DiagnosticsEnabled;
    public string SettingsDescription => $"debug mode {IsDebugEnabled}; automatic diagnostic interval 60s";
    private void Write(LogTopic topic, string message, Dictionary<string, object?> fields)
    {
        if (!IsDebugEnabled) return;
        LogFields evidence = fields;
        DiagnosticLog.Write(_logger, "InfiniteTorches", topic, message, evidence.Take(8), LogLevel.Debug);
        if (VerboseEnabled && fields.Count > 8) DiagnosticLog.Write(_logger, "InfiniteTorches", topic, fields.TryGetValue("object", out var id) && id != null ? $"Refill evidence for object {id}." : "Refill evidence for the last minute.", evidence.Skip(8), LogLevel.Debug, verbose: true);
    }
    private static (string Message, Dictionary<string, object?> Fields) Payload(string text, int maximum = 8)
    {
        var segments = text.Split(new[] { "; " }, StringSplitOptions.None);
        var fields = new Dictionary<string, object?>();
        foreach (string segment in segments)
        {
            if (fields.Count >= maximum) break;
            int equals = segment.IndexOf('=');
            if (equals < 0) continue;
            string key = new string(segment.Substring(0, equals).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
            string value = segment.Substring(equals + 1).TrimEnd('.');
            foreach (string suffix in new[] { "_now", "_pass", "_interval" }) if (key.EndsWith(suffix, StringComparison.Ordinal)) { key = key.Substring(0, key.Length - suffix.Length); break; }
            fields[key] = long.TryParse(value, out long count) ? count :
                bool.TryParse(value, out bool flag) ? flag : (object)value;
        }
        return (segments[0].Contains("=") ? "Refill request sent; client application unconfirmed" : segments[0], fields);
    }
    public void LogRefillRequest(float now, Func<string> describe)
    {
        if (!IsDebugEnabled || !_budget.Reserve(now, "refill_requests", RefillDetailLimit)) return;
        var payload = Payload(describe()); Write(InfiniteTorchesLogTopics.Refill, payload.Message, payload.Fields);
    }
    public void Request(float now, string id, string prefab, UnityEngine.Vector3 position, float before, float target)
    {
        if (!IsDebugEnabled || !_budget.Reserve(now, "refill_requests", RefillDetailLimit)) return;
        Write(InfiniteTorchesLogTopics.Refill, "Refill request sent; client application unconfirmed", new()
        { ["object"] = id, ["prefab"] = prefab, ["pos"] = position, ["fuel_before"] = before, ["fuel_target"] = target });
    }
    public void UnsupportedPrefabs(int count, IReadOnlyDictionary<string, int> reasons)
    {
        if (count <= 0) return;
        string grouped = string.Join(", ", reasons.OrderBy(pair => pair.Key).Select(pair => pair.Key + " (" + pair.Value + ")"));
        DiagnosticLog.Write(_logger, "InfiniteTorches", $"Skipped {count} unsupported prefab entries: {grouped}. Review Torches.Prefabs.", level: LogLevel.Warning);
    }
    public void RefillSummary(int fuelUpdates)
    {
        if (fuelUpdates <= 0) return;
        DiagnosticLog.Write(_logger, "InfiniteTorches", $"Fuel increase observed for {fuelUpdates} {(fuelUpdates == 1 ? "light source" : "light sources")}.");
    }
    public void Result(float now, RefillResult result)
    {
        if (!IsDebugEnabled || !_budget.Reserve(now, "refill_results", 6)) return;
        string message = result.Outcome == "fuel_increase" ? "Fuel increase observed after request; delivery acknowledgement unavailable" :
            result.Outcome == "timeout" ? "Sufficient fuel increase not observed; retry permitted if eligible" :
            result.Outcome == "owner_changed" ? "Owner changed; prior request tracking cleared" : "Object unavailable; request tracking cleared";
        var fields = new Dictionary<string, object?> { ["object"] = result.Object, ["outcome"] = result.Outcome,
            ["wait_s"] = result.WaitSeconds, ["fuel_before"] = result.Before,
            ["fuel_target"] = result.Target, ["fuel_observed"] = result.Fuel };
        if (result.Outcome == "owner_changed") { fields["requested_owner"] = result.RequestedOwner; fields["observed_owner"] = result.ObservedOwner; }
        Write(InfiniteTorchesLogTopics.Refill, message, fields);
    }
    public void Report(float now, Func<string> describe)
    {
        if (!IsDebugEnabled || !_budget.Reserve(now, "summary", 1, false)) return;
        bool rich = VerboseAfterNormalEnabled;
        var payload = Payload(describe(), rich ? int.MaxValue : 5);
        payload.Fields["interval_s"] = _reported ? Math.Max(0, now - _lastSummary) : 0;
        payload.Fields["omitted_messages"] = _budget.TakeOmitted();
        var totals = _totals?.Invoke() ?? new Dictionary<string, long>();
        foreach (var pair in totals)
        { if (!rich && payload.Fields.Count >= 8) break; _previous.TryGetValue(pair.Key, out long previous); payload.Fields[pair.Key] = Math.Max(0, pair.Value - previous); }
        Write(CoreLogTopics.Scope, payload.Message, payload.Fields);
        _previous = new Dictionary<string, long>(totals); _lastSummary = now; _reported = true;
    }
    public void Reset() { _budget.Reset(); _previous.Clear(); _lastSummary = 0; _reported = false; }
}
