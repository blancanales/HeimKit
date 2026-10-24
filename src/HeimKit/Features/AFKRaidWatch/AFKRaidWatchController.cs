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
using HeimKit.Core.Players;
using HeimKit.Features.AFKRaidWatch.Models;
using UnityEngine;
namespace HeimKit.Features.AFKRaidWatch;
public sealed class AFKRaidWatchController : IDisposable
{
    internal static AFKRaidWatchController? Active { get; private set; }
    private readonly AFKRaidWatch _module;
    private readonly ManualLogSource _logger;
    private readonly PlayerServices _players;
    private PlayerActivityService.Lease? _lease;
    private AFKRaidWatchPatches? _patches;
    private readonly AFKRaidWatchDiagnostics _diagnostics;
    private readonly AFKRaidWatchSelectionNotices _selectionNotices;
    private readonly ObservationCounters _hooks = new();
    private long _candidates, _suppressed;
    private RaidStartObservation? _lastOutcome;
    private RaidScheduleObservation? _schedule;
    internal bool DebugEnabled => _diagnostics.Enabled;
    public AFKRaidWatchController(AFKRaidWatch module, AFKRaidWatchSettings settings, ManualLogSource logger, PlayerServices players)
    {
        _module = module; _logger = logger; _players = players; _diagnostics = new(logger, settings.DebugLogging); _selectionNotices = new(logger);
        try
        {
            _lease = players.Activity.Acquire(() => _diagnostics.Enabled);
            _patches = new(logger); Active = this;
        }
        catch { Dispose(); throw; }
        if (DebugEnabled) if (DiagnosticLog.DebugAvailable(logger, module.Name)) DiagnosticLog.Write(logger, module.Name, AFKRaidWatchLogTopics.Raids, "Registered native raid hooks.", new (string, object?)[] { ("raid_hooks", _patches.RegisteredHooks) }, LogLevel.Debug);
    }
    internal List<Vector3> Filter(RandomEvent ev, List<Vector3> points)
    {
        if (_lease == null || !_players.Activity.Available) { _hooks.Hit("filter_not_ready_native_fallback"); return points; }
        var players = _lease.Service.Snapshot(Time.unscaledTime);
        if (!_lease.Service.Reliable) { _hooks.Hit("filter_not_ready_native_fallback"); return points; }
        var remaining = new List<Vector3>(points.Count);
        foreach (var point in points)
        {
            bool suppressed = AFKRaidWatchRules.Suppress(point.x, point.z, ev.m_eventRange, players, out string reason);
            _candidates++; if (suppressed) { _suppressed++; _selectionNotices.Excluded(); } else remaining.Add(point);
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
    internal void BeginSelection() => _selectionNotices.Begin();
    internal void EndSelection(bool failed) => _selectionNotices.End(failed);
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
        if (!DebugEnabled) { _lastOutcome = null; _schedule = null; _diagnostics.SuspendReports(); return; }
        if (_diagnostics.ReadyToPoll(now)) _diagnostics.Report(now, () => new RaidWatchStatus { Connected = _players.Presence.ConnectedCount, Candidates = _candidates, Suppressed = _suppressed, Hooks = _hooks, Outcome = _lastOutcome, Schedule = _schedule });
    }
    public void Dispose()
    {
        _selectionNotices.Reset();
        if (ReferenceEquals(Active, this)) Active = null;
        var patches = _patches; var lease = _lease; _patches = null; _lease = null;
        if (!ModuleCleanup.Run(_logger, _module.Name, () => patches?.Dispose(), () => lease?.Dispose()))
            throw new InvalidOperationException("Controller cleanup did not complete; paused state cannot be confirmed.");
    }
}
