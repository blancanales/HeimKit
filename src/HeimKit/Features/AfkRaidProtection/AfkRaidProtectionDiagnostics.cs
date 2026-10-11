using BepInEx.Logging;
using System;
using System.Collections.Generic;
using HeimKit.Features.AfkRaidProtection.Models;
using HeimKit.Configuration;
using HeimKit.Shared;
using UnityEngine;
namespace HeimKit.Features.AfkRaidProtection;
public sealed class AfkRaidProtectionDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly DebugLoggingPolicy _policy;
    private readonly DiagnosticBudget _budget = new();
    public bool Enabled => _policy.Enabled;
    private readonly Dictionary<(string, float, float), string> _candidateStates = new();
    private readonly ActivitySamples _players = new();
    private long _unchanged;
    private float _lastWarning = float.NegativeInfinity;
    public AfkRaidProtectionDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; }
    public void Report(float now, int players, int idle, long candidates, long suppressed, HookCounters? hooks = null, RaidStartObservation? outcome = null, HookCounters? observer = null, RaidScheduleObservation? schedule = null)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "SUMMARY", 1, false)) return;
        DiagnosticLog.Write(_log, "AfkRaidProtection", DiagnosticEvent.Summary, "Automatic raid candidate observations", new (string, object?)[]
        { ("players_now", players), ("inferred_idle_now", idle), ("candidates_evaluated_total", candidates), ("candidates_suppressed_total", suppressed), ("scope", "local_native_event_range"), ("existing_raids", "unchanged"), ("omitted_messages_interval", _budget.TakeOmitted()), ("unchanged_candidate_samples_interval", TakeUnchanged()), ("hook_counts_total", hooks?.Format() ?? "not_observed"), ("routed_observer_counts_total", observer?.Format() ?? "not_observed"),
          ("native_event_outcome", outcome?.Outcome ?? "not_observed"), ("native_event_origin", outcome?.Origin ?? "unknown"), ("native_event_outcome_age_s", outcome == null ? (float?)null : Math.Max(0, now - outcome.ObservedAt)), ("event_start_cause", "unknown"), ("pending_afk_changes_now", _players.Pending), ("afk_transitions_interval", _players.TransitionsInterval), ("pending_afk_states_replaced_interval", _players.ReplacedPendingInterval), ("native_schedule_gate", schedule?.Gate), ("native_schedule_age_s", schedule == null ? (float?)null : Math.Max(0, now - schedule.ObservedAt)), ("native_event_timer_game_s", schedule?.Timer), ("native_interval_minutes", schedule?.IntervalMinutes), ("native_event_rate", schedule?.EventRate), ("native_event_chance_setting", schedule?.Chance), ("chance_roll", "unobserved") });
        _players.IntervalReported();
    }
    private long TakeUnchanged() { long result = _unchanged; _unchanged = 0; return result; }
    public void Sample(float now, string name, Vector3 point, bool suppressed, string reason, Func<RaidLocalObservation>? population = null)
    {
        if (!_policy.Enabled) return;
        var key = (name, point.x, point.z);
        if (_candidateStates.TryGetValue(key, out var previous) && previous == reason) { _unchanged++; return; }
        if (!_budget.Reserve(now, "RESULT", 6)) return;
        if (_candidateStates.Count >= 256) _candidateStates.Clear();
        _candidateStates[key] = reason;
        RaidLocalObservation? local = null;
        try { local = population?.Invoke(); } catch (Exception error) { Warning(now, error); }
        DiagnosticLog.Write(_log, "AfkRaidProtection", DiagnosticEvent.Result, suppressed ? "Automatic raid location excluded: all nearby players inferred idle" : "Native raid location retained", new (string, object?)[]
        { ("event", name), ("x", point.x), ("y", point.y), ("z", point.z), ("suppressed", suppressed), ("reason", reason), ("raid_started", "not_observed"), ("native_candidate_eligible", true), ("native_event_range_m", local?.Range), ("unknown_actors", local?.Unknown), ("dungeon_actors", local?.Dungeon), ("outside_actors", local?.Outside),
          ("nearby_idle", local?.NearbyIdle), ("nearby_active", local?.NearbyActive), ("nearby_bed", local?.NearbyBed), ("nearby_dead_or_loading", local?.NearbyDead) });
    }
    public void Players(float now, IEnumerable<PlayerActivityView> players)
    {
        if (!_policy.Enabled) { _players.Reset(); _candidateStates.Clear(); return; }
        _players.Capture(now, players);
        if (!_budget.Reserve(now, "PLAYERS", 1, false)) return;
        foreach (var p in _players.Drain(6))
            DiagnosticLog.Write(_log, "AfkRaidProtection", DiagnosticEvent.Detail, p.Reason, new (string, object?)[]
            { ("peer", p.Peer), ("session", p.Session), ("state_revision", p.StateRevision), ("known", p.Known), ("inferred_idle", p.Idle), ("idle_observed_s", p.IdleSeconds), ("last_observed_signal", p.LastSignal), ("grace_remaining_s_at_sample", p.GraceRemaining), ("state_sample_age_s", System.Math.Max(0, now - p.ObservedAt)), ("recovery", p.Recovery) });
    }
    public void Outcome(RaidStartObservation outcome)
    {
        if (!_policy.Enabled || !_budget.Reserve(outcome.ObservedAt, "OUTCOME", 6)) return;
        DiagnosticLog.Write(_log, "AfkRaidProtection", DiagnosticEvent.Result, "Native event setter observed", new (string, object?)[]
        { ("outcome", outcome.Outcome), ("requested_event", outcome.Requested), ("current_event", outcome.Current), ("state_changed", outcome.StateChanged), ("native_exception", outcome.Failed), ("origin", outcome.Origin), ("cause", "unknown"), ("enemy_spawns", "unobservable_here"), ("clients_acknowledged", "unobservable") });
    }
    public void StartReturned(float now, string origin, string? before, string? after, bool changed, bool failed)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "OUTCOME", 6)) return;
        DiagnosticLog.Write(_log, "AfkRaidProtection", DiagnosticEvent.Result, "Native random-event start request returned", new (string, object?)[]
        { ("outcome", failed ? "native_exception" : changed ? "server_event_state_changed" : "no_event_state_change"), ("origin", origin), ("before_event", before), ("after_event", after), ("cause", "unknown"), ("enemy_spawns", "unobservable_here") });
    }
    public void Warning(float now, Exception error)
    {
        if (now >= _lastWarning && now - _lastWarning < 60) return;
        _lastWarning = now;
        DiagnosticLog.Write(_log, "AfkRaidProtection", DiagnosticEvent.Lifecycle, "Diagnostic observation unavailable; native behavior unchanged: " + error.Message, level: LogLevel.Warning);
    }
}
