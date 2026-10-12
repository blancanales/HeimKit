using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
using UnityEngine;
using HeimKit.Features.AFKRaidWatch.Models;
namespace HeimKit.Features.AFKRaidWatch;
public sealed class AFKRaidWatchDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly DebugLoggingPolicy _policy;
    private readonly DiagnosticBudget _budget = new();
    private readonly SummaryCadence _summary = new();
    public bool Enabled => _policy.Enabled;
    private float _lastWarning = float.NegativeInfinity;
    private readonly Dictionary<(string, float, float), string> _candidateStates = new();
    private long _unchanged;
    public AFKRaidWatchDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; }
    private void Write(DiagnosticEvent category, string message, IEnumerable<(string Key, object? Value)>? fields = null, LogLevel level = LogLevel.Debug)
        => DiagnosticLog.Write(_log, "AFKRaidWatch", category, message, fields, level);
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
            Write(DiagnosticEvent.Summary, "Heartbeat: last relevant observation unchanged", new (string, object?)[] { ("observation_age_s", state.Outcome == null ? (float?)null : Math.Max(0, now - state.Outcome.ObservedAt)), ("omitted_messages_interval", _budget.TakeOmitted()) });
            return;
        }
        Write(DiagnosticEvent.Summary, "Automatic raid-location evaluations", new (string, object?)[] {
            ("candidates_evaluated_total", state.Candidates), ("candidates_excluded_total", state.Suppressed),
            ("native_event_outcome", state.Outcome?.Outcome ?? "not_observed"),
            ("native_event_origin", state.Outcome?.Origin ?? "unknown"),
            ("native_event_outcome_age_s", state.Outcome == null ? (float?)null : Math.Max(0, now - state.Outcome.ObservedAt)),
            ("cause", "unknown"), ("unchanged_candidate_samples_interval", TakeUnchanged()), ("omitted_messages_interval", _budget.TakeOmitted()) });
        if (_budget.Reserve(now, "HOOK_DETAIL", 1, false)) Write(DiagnosticEvent.Detail, "Native raid-selection evidence", new (string, object?)[] {
            ("raid_hook_counts_total", state.Hooks?.Format() ?? "not_observed"), ("native_schedule_gate", state.Schedule?.Gate),
            ("native_schedule_age_s", state.Schedule == null ? (float?)null : Math.Max(0, now - state.Schedule.ObservedAt)),
            ("chance_roll", "unobserved") });
    }
    private long TakeUnchanged() { long result = _unchanged; _unchanged = 0; return result; }
    public void Sample(float now, string name, Vector3 point, bool suppressed, string reason, Func<RaidLocalObservation>? population = null)
    {
        if (!_policy.Enabled) return;
        var key = (name, point.x, point.z);
        if (_candidateStates.TryGetValue(key, out var previous) && previous == reason) { _unchanged++; return; }
        if (!_budget.Reserve(now, "RAID_CANDIDATE", 6)) return;
        if (_candidateStates.Count >= 256) _candidateStates.Clear();
        _candidateStates[key] = reason;
        RaidLocalObservation? local = null;
        try { local = population?.Invoke(); } catch (Exception error) { Warning(now, error); }
        Write(DiagnosticEvent.Result, suppressed ? "Automatic raid location excluded: all nearby players inferred idle" : "Native raid location retained", new (string, object?)[]
        { ("event", name), ("x", point.x), ("y", point.y), ("z", point.z), ("suppressed", suppressed), ("reason", reason), ("raid_started", "not_observed"), ("native_candidate_eligible", true), ("native_event_range_m", local?.Range), ("unknown_actors", local?.Unknown), ("dungeon_actors", local?.Dungeon), ("outside_actors", local?.Outside),
          ("nearby_idle", local?.NearbyIdle), ("nearby_active", local?.NearbyActive), ("nearby_bed", local?.NearbyBed), ("nearby_dead_or_loading", local?.NearbyDead) });
    }
    public void Outcome(RaidStartObservation outcome)
    {
        if (!_policy.Enabled || !_budget.Reserve(outcome.ObservedAt, "RAID_OUTCOME", 6)) return;
        Write(DiagnosticEvent.Result, "Native event setter observed", new (string, object?)[]
        { ("outcome", outcome.Outcome), ("requested_event", outcome.Requested), ("current_event", outcome.Current), ("state_changed", outcome.StateChanged), ("native_exception", outcome.Failed), ("origin", outcome.Origin), ("cause", "unknown"), ("enemy_spawns", "unobservable_here"), ("clients_acknowledged", "unobservable") });
    }
    public void StartReturned(float now, string origin, string? before, string? after, bool changed, bool failed)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "RAID_OUTCOME", 6)) return;
        Write(DiagnosticEvent.Result, "Native random-event start request returned", new (string, object?)[]
        { ("outcome", failed ? "native_exception" : changed ? "server_event_state_changed" : "no_event_state_change"), ("origin", origin), ("before_event", before), ("after_event", after), ("cause", "unknown"), ("enemy_spawns", "unobservable_here") });
    }
    public void Warning(float now, Exception error)
    {
        if (now >= _lastWarning && now - _lastWarning < 300) return;
        _lastWarning = now;
        Write(DiagnosticEvent.Lifecycle, "Diagnostic observation unavailable; native behavior unchanged: " + error.Message, level: LogLevel.Warning);
    }
}
