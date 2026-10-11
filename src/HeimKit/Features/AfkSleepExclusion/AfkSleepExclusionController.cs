using System;
using BepInEx.Logging;
using HeimKit.Shared;
using UnityEngine;
namespace HeimKit.Features.AfkSleepExclusion;
public sealed class AfkSleepExclusionController : IDisposable
{
    private IDisposable? _vote;
    private PlayerActivityService.Lease? _lease;
    private readonly AfkSleepExclusionDiagnostics _diagnostics;
    private SleepDecision? _last;
    private long _evaluations;
    private float? _lastVote;
    private readonly HookCounters _hooks = new();
    private NativeSleepObservation? _native, _lastOutcome;
    public AfkSleepExclusionController(AfkSleepExclusion module, AfkSleepExclusionSettings settings, ManualLogSource logger)
    {
        _diagnostics = new(logger, settings.DebugLogging);
        foreach (string key in new[] { "vote_hook_calls", "update_sleeping_invocations", "empty_vote_rosters", "no_living_sleeper_votes", "unknown_actor_native_fallbacks", "vote_not_ready_fallback", "vote_returned_true", "vote_returned_false", "native_vote_exceptions", "native_sleep_update_exceptions", "skip_to_morning_invocations", "sleep_probe_failures", "time_skip_activations_observed", "time_skip_endings_observed" }) _hooks.Add(key, 0);
        try { _lease = PlayerActivityService.Acquire(settings.Activity.Snapshot(), logger);
            _vote = SleepVoteCoordinator.RegisterExclusion(_lease.Service, Observe, module.Fail, () => _diagnostics.Enabled, ObserveHook, logger); } catch { Dispose(); throw; }
        DiagnosticLog.Write(logger, module.Name, DiagnosticEvent.Lifecycle, "Native hooks registered", new (string, object?)[] { ("hooks", SleepVoteCoordinator.RegisteredHooks), ("registered", true) });
    }
    private void ObserveHook(string hook, NativeSleepObservation? evidence)
    {
        _hooks.Hit(hook);
        if (evidence != null && _diagnostics.Enabled) { _native = evidence;
            if (evidence.Outcome == "time_skip_became_active" || evidence.Outcome == "time_skip_ended" || evidence.Outcome == "sleep_session_ended_server_state" || evidence.Failed)
            { _lastOutcome = evidence; _hooks.Hit(evidence.Failed ? "native_failures_observed" : evidence.Outcome == "time_skip_became_active" ? "time_skip_activations_observed" : "time_skip_endings_observed"); }
            _diagnostics.Native(evidence); }
    }
    private void Observe(SleepDecision decision) { _last = decision; _lastVote = Time.unscaledTime; _evaluations++; }
    public void Tick(float now) { if (!_diagnostics.Enabled) { _native = null; _lastOutcome = null; } var players = _lease?.Service.Snapshot(now); if (players != null) _diagnostics.Players(now, players, _lease?.Service.Hooks); _diagnostics.Report(now, _last, _evaluations, _lastVote == null ? null : Math.Max(0, now - _lastVote.Value), _hooks, _native, _lastOutcome); }
    public void Dispose() { _vote?.Dispose(); _vote = null; _lease?.Dispose(); _lease = null; }
}
