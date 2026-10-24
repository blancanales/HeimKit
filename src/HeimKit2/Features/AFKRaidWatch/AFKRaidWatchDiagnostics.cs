using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Configuration;
using UnityEngine;
using HeimKit.Features.AFKRaidWatch.Models;
namespace HeimKit.Features.AFKRaidWatch;
public sealed class AFKRaidWatchDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly DebugLoggingPolicy _policy;
    private readonly SummaryCadence _summary = new();
    public bool Enabled => DiagnosticsEnabled;
    private float _lastWarning = float.NegativeInfinity;
    private readonly Dictionary<(string, float, float), string> _candidateStates = new();
    private long _unchanged;
    public AFKRaidWatchDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; ConfigureDiagnostics(() => policy.Enabled, log, "AFKRaidWatch"); }
    private void Write(LogTopic topic, string message, LogFields fields = default, LogLevel level = LogLevel.Debug, bool verbose = false)
        => DiagnosticLog.Write(_log, "AFKRaidWatch", topic, message, fields, level, verbose: verbose);
    public void SuspendReports() { _candidateStates.Clear(); _summary.Reset(); }
    public bool ReadyToPoll(float now) => Enabled && _summary.PollReady(now);
    public void Report(float now, Func<RaidWatchStatus> snapshot)
    {
        if (!Enabled) { _candidateStates.Clear(); _summary.Reset(); return; }
        if (!_summary.CanPoll(now)) return;
        var state = snapshot();
        if (state.Connected == 0 || state.Candidates == 0) { _summary.Reset(); return; }
        string key = $"{state.Candidates}:{state.Suppressed}:{state.Outcome?.Outcome}:{state.Outcome?.Current}:{state.Outcome?.Requested}:{state.Outcome?.Origin}";
        if (!_summary.ShouldEmit(now, key)) return;
        if (!_summary.Changed)
        {
            Write(CoreLogTopics.Scope, "Relevant observations remain unchanged.", new (string, object?)[] { ("observation_age_s", state.Outcome == null ? (float?)null : Math.Max(0, now - state.Outcome.ObservedAt)), ("omitted_messages", _budget.TakeOmitted()) });
            return;
        }
        Write(CoreLogTopics.Scope, "Evaluated automatic raid locations in the last minute.", new (string, object?)[] { ("lifetime_candidates_evaluated", state.Candidates), ("lifetime_candidates_excluded", state.Suppressed), ("native_event_outcome", state.Outcome?.Outcome ?? "not_observed"), ("native_event_origin", state.Outcome?.Origin ?? "unknown"), ("native_event_outcome_age_s", state.Outcome == null ? (float?)null : Math.Max(0, now - state.Outcome.ObservedAt)), ("cause", "unknown"), ("unchanged_candidate_samples", TakeUnchanged()), ("omitted_messages", _budget.TakeOmitted()) });
        if (_budget.Reserve(now, "hook_activity", 1, false)) Write(AFKRaidWatchLogTopics.Raids, "Observed native raid selection in the last minute.", new (string, object?)[] { ("lifetime_raid_hook_counts", state.Hooks?.Format() ?? "not_observed"), ("native_schedule_gate", state.Schedule?.Gate), ("native_schedule_age_s", state.Schedule == null ? (float?)null : Math.Max(0, now - state.Schedule.ObservedAt)), ("chance_roll", "unobserved") });
    }
    private long TakeUnchanged() { long result = _unchanged; _unchanged = 0; return result; }
    public void Sample(float now, string name, Vector3 point, bool suppressed, string reason, Func<RaidLocalObservation>? population = null)
    {
        if (!DiagnosticsEnabled) return;
        var key = (name, point.x, point.z);
        if (_candidateStates.TryGetValue(key, out var previous) && previous == reason) { _unchanged++; return; }
        if (!_budget.Reserve(now, "raid_candidates", 6)) return;
        if (_candidateStates.Count >= 256) _candidateStates.Clear();
        _candidateStates[key] = reason;
        RaidLocalObservation? local = null;
        try { local = population?.Invoke(); } catch (Exception error) { Warning(now, error); }
        { Write(AFKRaidWatchLogTopics.Raids, suppressed ? "Automatic raid location excluded: all nearby players inferred idle" : "Native raid location retained", new (string, object?)[] { ("event", name), ("x", point.x), ("y", point.y), ("z", point.z), ("suppressed", suppressed), ("reason", reason), ("raid_started", "not_observed"), ("native_candidate_eligible", true) }); if (VerboseEnabled) Write(AFKRaidWatchLogTopics.Raids, $"Nearby player evidence for raid location {name}.", new (string, object?)[] { ("native_event_range_m", local?.Range), ("unknown_actors", local?.Unknown), ("dungeon_actors", local?.Dungeon), ("outside_actors", local?.Outside), ("nearby_idle", local?.NearbyIdle), ("nearby_active", local?.NearbyActive), ("nearby_bed", local?.NearbyBed), ("nearby_dead_or_loading", local?.NearbyDead) }, verbose: true); }
    }
    public void Outcome(RaidStartObservation outcome)
    {
        if (!DiagnosticsEnabled || !_budget.Reserve(outcome.ObservedAt, "raid_outcomes", 6)) return;
        { Write(AFKRaidWatchLogTopics.Raids, "Observed the native event setter outcome.", new (string, object?)[] { ("outcome", outcome.Outcome), ("requested_event", outcome.Requested), ("current_event", outcome.Current), ("state_changed", outcome.StateChanged), ("native_exception", outcome.Failed), ("origin", outcome.Origin), ("cause", "unknown"), ("enemy_spawns", "unobservable_here") }); if (VerboseEnabled) Write(AFKRaidWatchLogTopics.Raids, $"Event setter evidence for outcome {outcome.Outcome}.", new (string, object?)[] { ("clients_acknowledged", "unobservable") }, verbose: true); }
    }
    public void StartReturned(float now, string origin, string? before, string? after, bool changed, bool failed)
    {
        if (!DiagnosticsEnabled || !_budget.Reserve(now, "raid_outcomes", 6)) return;
        Write(AFKRaidWatchLogTopics.Raids, "The native random-event start request returned.", new (string, object?)[] { ("outcome", failed ? "native_exception" : changed ? "server_event_state_changed" : "no_event_state_change"), ("origin", origin), ("before_event", before), ("after_event", after), ("cause", "unknown"), ("enemy_spawns", "unobservable_here") });
    }
    public void Warning(float now, Exception error)
    {
        if (now >= _lastWarning && now - _lastWarning < 300) return;
        _lastWarning = now;
        Write(CoreLogTopics.Lifecycle, "Diagnostic observation unavailable; native behavior unchanged: " + error.Message, level: LogLevel.Warning);
    }
}
