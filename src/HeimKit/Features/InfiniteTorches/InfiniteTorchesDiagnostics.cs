using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;
using HeimKit.Features.InfiniteTorches.Models;

namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorchesDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly Func<bool> _debugEnabled;
    private readonly DiagnosticBudget _budget = new();
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
    { _logger = logger; _debugEnabled = debugEnabled; _totals = totals; }
    public bool IsDebugEnabled => _debugEnabled() && DiagnosticLog.ServerActivity.Allowed;
    public string SettingsDescription => $"debug mode {IsDebugEnabled}; automatic diagnostic interval 60s";
    private void Write(DiagnosticEvent category, string message, Dictionary<string, object?> fields)
        => DiagnosticLog.Write(_logger, "Infinite Torches", category, message, fields.Select(p => (p.Key, p.Value)), LogLevel.Debug);
    private static (string Message, Dictionary<string, object?> Fields) Payload(string text)
    {
        var segments = text.Split(new[] { "; " }, StringSplitOptions.None);
        var fields = new Dictionary<string, object?>();
        foreach (string segment in segments)
        {
            int equals = segment.IndexOf('=');
            if (equals < 0) continue;
            string key = new string(segment.Substring(0, equals).Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
            string value = segment.Substring(equals + 1).TrimEnd('.');
            fields[key] = long.TryParse(value, out long count) ? count :
                bool.TryParse(value, out bool flag) ? flag : (object)value;
        }
        return (segments[0].Contains("=") ? "Refill request sent; client application unconfirmed" : segments[0], fields);
    }
    public void LogRefillRequest(float now, Func<string> describe)
    {
        if (!IsDebugEnabled || !_budget.Reserve(now, "ACTION", RefillDetailLimit)) return;
        var payload = Payload(describe()); Write(DiagnosticEvent.Action, payload.Message, payload.Fields);
    }
    public void Request(float now, string id, string prefab, string position, float before, float target)
    {
        if (!IsDebugEnabled || !_budget.Reserve(now, "ACTION", RefillDetailLimit)) return;
        Write(DiagnosticEvent.Action, "Refill request sent; client application unconfirmed", new()
        { ["object"] = id, ["prefab"] = prefab, ["position"] = position, ["fuel_before"] = before, ["fuel_target"] = target });
    }
    public void Result(float now, RefillResult result)
    {
        if (!IsDebugEnabled || !_budget.Reserve(now, "RESULT", 6)) return;
        string message = result.Outcome == "fuel_increase" ? "Fuel increase observed after request; delivery acknowledgement unavailable" :
            result.Outcome == "timeout" ? "Sufficient fuel increase not observed; retry permitted if eligible" :
            result.Outcome == "owner_changed" ? "Owner changed; prior request tracking cleared" : "Object unavailable; request tracking cleared";
        var fields = new Dictionary<string, object?> { ["object"] = result.Object, ["outcome"] = result.Outcome,
            ["source"] = "replicated_state", ["wait_s"] = result.WaitSeconds, ["fuel_before"] = result.Before,
            ["fuel_target"] = result.Target, ["fuel_observed"] = result.Fuel };
        if (result.Outcome == "owner_changed") { fields["requested_owner"] = result.RequestedOwner; fields["observed_owner"] = result.ObservedOwner; }
        Write(DiagnosticEvent.Result, message, fields);
    }
    public void Report(float now, Func<string> describe)
    {
        if (!IsDebugEnabled || !_budget.Reserve(now, "SUMMARY", 1, false)) return;
        var payload = Payload(describe());
        payload.Fields["interval_s"] = _reported ? Math.Max(0, now - _lastSummary) : 0;
        payload.Fields["omitted_messages_interval"] = _budget.TakeOmitted();
        var totals = _totals?.Invoke() ?? new Dictionary<string, long>();
        foreach (var pair in totals)
        { _previous.TryGetValue(pair.Key, out long previous); payload.Fields[pair.Key + "_interval"] = Math.Max(0, pair.Value - previous); }
        Write(DiagnosticEvent.Summary, payload.Message, payload.Fields);
        _previous = new Dictionary<string, long>(totals); _lastSummary = now; _reported = true;
    }
    public void Reset() { _budget.Reset(); _previous.Clear(); _lastSummary = 0; _reported = false; }
}
