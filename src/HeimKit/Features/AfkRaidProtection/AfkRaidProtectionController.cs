using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HeimKit.Shared;
using HeimKit.Features.AfkRaidProtection.Models;
using UnityEngine;
namespace HeimKit.Features.AfkRaidProtection;
public sealed class AfkRaidProtectionController : IDisposable
{
    internal static AfkRaidProtectionController? Active { get; private set; }
    private readonly AfkRaidProtection _module;
    private PlayerActivityService.Lease? _lease;
    private AfkRaidProtectionPatches? _patches;
    private readonly AfkRaidProtectionDiagnostics _diagnostics;
    private long _candidates, _suppressed;
    private readonly HookCounters _hooks = new();
    private RaidStartObservation? _lastOutcome;
    private RaidScheduleObservation? _schedule;
    internal bool DebugEnabled => _diagnostics.Enabled;
    public AfkRaidProtectionController(AfkRaidProtection module, AfkRaidProtectionSettings settings, ManualLogSource logger)
    {
        _module = module; _diagnostics = new(logger, settings.DebugLogging);
        foreach (string key in new[] { "automatic_update_invocations", "get_valid_event_points_invocations", "get_valid_event_points_results", "empty_native_candidate_results", "empty_possible_events_after_filter", "native_candidate_records", "native_valid_points_before_afk", "native_records_rejected_before_afk", "native_keys_passed", "native_keys_rejected", "native_biome_passed", "native_biome_rejected", "native_base_passed", "native_base_rejected", "automatic_start_requests", "explicit_or_other_start_requests", "start_returned_changed_state", "start_returned_unchanged_state", "set_random_event_invocations", "event_starts_observed", "outside_automatic_scope_native_retained", "standalone_native_retained", "non_random_native_retained", "filter_not_ready_native_fallback", "diagnostic_probe_failures", "native_automatic_update_exceptions", "native_candidate_exceptions", "native_start_request_exceptions", "native_event_setter_exceptions" }) _hooks.Add(key, 0);
        try { _lease = PlayerActivityService.Acquire(settings.Activity.Snapshot(), logger); _patches = new(logger); Active = this; }
        catch { Dispose(); throw; }
        DiagnosticLog.Write(logger, module.Name, DiagnosticEvent.Lifecycle, "Native hooks registered", new (string, object?)[] { ("hooks", _patches.RegisteredHooks), ("registered", true) });
    }
    internal List<Vector3> Filter(RandomEvent ev, List<Vector3> points)
    {
        if (_lease == null || !PlayerActivityService.Ready) { _hooks.Hit("filter_not_ready_native_fallback"); return points; }
        var players = _lease.Service.Snapshot(Time.unscaledTime);
        var remaining = new List<Vector3>(points.Count);
        foreach (var point in points)
        {
            bool suppressed = AfkRaidRules.Suppress(point.x, point.z, ev.m_eventRange, players, out string reason);
            _candidates++; if (suppressed) _suppressed++; else remaining.Add(point);
            if (_diagnostics.Enabled) try { _diagnostics.Sample(Time.unscaledTime, ev.m_name, point, suppressed, reason, () => RaidLocalObservation.Read(point.x, point.z, ev.m_eventRange, players)); }
            catch (Exception error) { _diagnostics.Warning(Time.unscaledTime, error); }
        }
        return remaining;
    }
    internal void ObserveSchedule(RandEventSystem system, float timer)
    {
        if (!DebugEnabled) return;
        float now = Time.unscaledTime;
        if (_schedule != null && now >= _schedule.ObservedAt && now - _schedule.ObservedAt < 1) return;
        _schedule = new RaidScheduleObservation { ObservedAt = now, Timer = timer, IntervalMinutes = system.m_eventIntervalMin, EventRate = Game.m_eventRate, Chance = system.m_eventChance };
    }
    internal void NativeCandidates(int records, int points)
    {
        _hooks.Add("native_candidate_records", records); _hooks.Add("native_valid_points_before_afk", points);
        _hooks.Add("native_records_rejected_before_afk", Math.Max(0, records - points));
        if (points == 0) _hooks.Hit("empty_native_candidate_results");
    }
    internal void Hook(string name) => _hooks.Hit(name);
    internal void ObserveOutcome(RaidStartObservation outcome)
    {
        if (!DebugEnabled) return;
        _lastOutcome = outcome;
        if (outcome.Started) _hooks.Hit("event_starts_observed");
        _diagnostics.Outcome(outcome);
    }
    internal void ObserveStartReturned(float now, string origin, string? before, string? after, bool changed, bool failed) => _diagnostics.StartReturned(now, origin, before, after, changed, failed);
    internal void ObservationFailed(Exception error) { _hooks.Hit("diagnostic_probe_failures"); _diagnostics.Warning(Time.unscaledTime, error); }
    internal void Fail(Exception error) => _module.Fail(error);
    public void Tick(float now)
    {
        var players = _lease?.Service.Snapshot(now);
        if (!_diagnostics.Enabled) { _lastOutcome = null; _schedule = null; }
        if (players != null) _diagnostics.Players(now, players);
        _diagnostics.Report(now, players?.Count ?? 0, players?.Count(p => p.Idle) ?? 0, _candidates, _suppressed, _hooks, _lastOutcome, _lease?.Service.Hooks, _schedule);
    }
    public void Dispose()
    {
        if (ReferenceEquals(Active, this)) Active = null;
        _patches?.Dispose(); _patches = null; _lease?.Dispose(); _lease = null;
    }
}
