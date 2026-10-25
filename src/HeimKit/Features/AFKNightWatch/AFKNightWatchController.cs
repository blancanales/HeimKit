using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
using HeimKit.Core.Players.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HeimKit.Features.AFKNightWatch.Models;
using UnityEngine;
namespace HeimKit.Features.AFKNightWatch;
public sealed class AFKNightWatchController : IDisposable
{
    private readonly AFKNightWatch _module;
    private readonly ManualLogSource _logger;
    private PlayerActivityService.Lease? _lease;
    private readonly AFKNightWatchDiagnostics _diagnostics;
    private readonly AFKNightWatchSleepNotices _sleepNotices;
    private long _evaluations, _sleepOutcomes;
    private readonly PlayerServices _players;
    private IDisposable? _sleepVote;
    private SleepDecision? _lastVote;
    private float? _lastVoteAt;
    private NativeSleepObservation? _nativeSleep, _lastSleepOutcome;
    private readonly AFKNightWatchSettings _settings;
    private readonly ObservationCounters _hooks = new();
    private float _reportNow;
    private readonly Func<NightWatchStatus> _snapshot;
    internal bool DebugEnabled => _diagnostics.Enabled;
    public AFKNightWatchController(AFKNightWatch module, AFKNightWatchSettings settings, ManualLogSource logger, PlayerServices players)
    {
        _snapshot = SnapshotStatus;
        _module = module; _logger = logger; _players = players; _settings = settings; _diagnostics = new(logger, settings.DebugLogging); _sleepNotices = new(logger, vote => _diagnostics.SleepStarted(vote, settings));
        foreach (string key in new[] { "vote_hook_calls", "update_sleeping_invocations", "empty_vote_rosters", "no_living_sleeper_votes", "unknown_actor_native_fallbacks", "vote_not_ready_fallback", "vote_returned_true", "vote_returned_false", "native_vote_exceptions", "native_sleep_update_exceptions", "skip_to_morning_invocations", "sleep_probe_failures", "time_skip_activations_observed", "time_skip_endings_observed" }) _hooks.Add(key, 0);
        try
        {
            if (settings.RequiresActivity) _lease = players.Activity.Acquire(() => _diagnostics.Enabled);
            if (settings.MajoritySleepEnabled.Value || settings.ExcludeAFKFromSleep.Value)
            {
                Func<(int, bool)>? quorum = settings.MajoritySleepEnabled.Value ? () => (settings.RequiredSleepingPercent.Value, settings.RequireMoreThanPercent.Value) : null;
                _sleepVote = AFKNightWatchSleepVoteCoordinator.Register(quorum, settings.ExcludeAFKFromSleep.Value ? _lease!.Service : null, ObserveVote, module.Fail, () => _diagnostics.Enabled, ObserveSleepHook, logger, module.Name, players.Presence, _sleepNotices.Observe, () => settings.SleepDelaySeconds.Value);
            }
        }
        catch { Dispose(); throw; }
        if (_diagnostics.Enabled)
            if (DiagnosticLog.DebugAvailable(logger, module.Name)) DiagnosticLog.Write(logger, module.Name, AFKNightWatchLogTopics.Sleep, "Registered native processing hooks.", new (string, object?)[] { ("sleep_hooks", _sleepVote == null ? "none" : AFKNightWatchSleepVoteCoordinator.RegisteredHooks), ("registered", true), ("log_schema", 1) }, LogLevel.Debug);
    }
    private void ObserveVote(SleepDecision vote) { _lastVote = vote; _lastVoteAt = Time.unscaledTime; _evaluations++; }
    private void ObserveSleepHook(string hook, NativeSleepObservation? evidence)
    {
        _hooks.Hit(hook);
        if (evidence == null || !_diagnostics.Enabled) return;
        _nativeSleep = evidence;
        if (evidence.Outcome == "time_skip_became_active" || evidence.Outcome == "time_skip_ended" || evidence.Outcome == "sleep_session_ended_server_state" || evidence.Failed)
        {
            _lastSleepOutcome = evidence;
            if (!evidence.Failed) _sleepOutcomes++;
            _hooks.Hit(evidence.Failed ? "native_failures_observed" : evidence.Outcome == "time_skip_became_active" ? "time_skip_activations_observed" : "time_skip_endings_observed");
        }
        _diagnostics.Native(evidence);
    }
    public void Tick(float now)
    {
        if (!_diagnostics.Enabled) { _nativeSleep = null; _lastSleepOutcome = null; _diagnostics.SuspendReports(); return; }
        if (_diagnostics.ReadyToPoll(now)) { _reportNow = now; _diagnostics.Report(now, _snapshot); }
    }
    private NightWatchStatus SnapshotStatus() => new NightWatchStatus {
            Vote = _lastVote, VoteAge = _lastVoteAt == null ? null : Math.Max(0, _reportNow - _lastVoteAt.Value), Votes = _evaluations, Outcomes = _sleepOutcomes,
            Players = _players.Presence.ConnectedCount, Hooks = _hooks, Sleep = _nativeSleep, SleepOutcome = _lastSleepOutcome,
            MajorityEnabled = _settings.MajoritySleepEnabled.Value, ExclusionEnabled = _settings.ExcludeAFKFromSleep.Value };

    public void Dispose()
    {
        _sleepNotices.Reset();
        var sleepVote = _sleepVote; var lease = _lease; _sleepVote = null; _lease = null;
        if (!ModuleCleanup.Run(_logger, _module.Name, () => sleepVote?.Dispose(), () => lease?.Dispose()))
            throw new InvalidOperationException("Controller cleanup did not complete; paused state cannot be confirmed.");
    }
}
