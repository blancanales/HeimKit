using BepInEx.Logging;
using System.Collections.Generic;
using System.Linq;
using HeimKit.Configuration;
using HeimKit.Shared;
namespace HeimKit.Features.AfkSleepExclusion;
public sealed class AfkSleepExclusionDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly DebugLoggingPolicy _policy;
    private readonly DiagnosticBudget _budget = new();
    public bool Enabled => _policy.Enabled;
    private string? _lastNativeKey;
    private long _unchangedNative;
    private readonly ActivitySamples _players = new();
    public AfkSleepExclusionDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; }
    public void Report(float now, SleepDecision? decision, long evaluations, float? voteAge = null, HookCounters? hooks = null, NativeSleepObservation? native = null, NativeSleepObservation? lastOutcome = null)
    {
        if (!_policy.Enabled) { _lastNativeKey = null; _unchangedNative = 0; _players.Reset(); return; }
        if (!_budget.Reserve(now, "SUMMARY", 1, false)) return;
        DiagnosticLog.Write(_log, "AfkSleepExclusion", DiagnosticEvent.Summary, decision == null ? "Native sleep vote not observed" : decision.Unknown ? decision.Sleepers > 0 ? "Unknown actor data; native vote retained" : "Unknown actor data; no known living bed sleeper" : decision.Met ? "Sleep quorum met; native time checks still apply" : "Sleep quorum not met", new (string, object?)[]
        { ("participants_last_vote", decision?.Participants), ("sleepers_last_vote", decision?.Sleepers), ("required_last_vote", decision?.Required), ("excluded_idle_last_vote", decision?.ExcludedIdle), ("quorum_met_last_vote", decision?.Met), ("native_vote_fallback", decision == null ? (bool?)null : decision.Unknown && decision.Sleepers > 0), ("votes_observed_total", evaluations), ("vote_age_s", voteAge), ("time_skip", lastOutcome?.Outcome ?? native?.Outcome ?? "not_observed"), ("last_outcome_age_s", lastOutcome == null ? (float?)null : System.Math.Max(0, now - lastOutcome.ObservedAt)),
          ("hook_counts_total", hooks?.Format() ?? "not_observed"), ("native_gate_last_update", native?.Gate), ("native_probe_age_s", native == null ? (float?)null : System.Math.Max(0, now - native.ObservedAt)),
          ("afternoon_last_update", native?.Afternoon), ("night_last_update", native?.Night), ("cooldown_remaining_s_last_update", native?.CooldownRemaining),
          ("native_outcome_cause", "unknown"), ("unchanged_native_samples_interval", TakeUnchanged()), ("omitted_messages_interval", _budget.TakeOmitted()), ("afk_transitions_interval", _players.TransitionsInterval), ("pending_afk_changes_now", _players.Pending), ("pending_afk_states_replaced_interval", _players.ReplacedPendingInterval) });
        _players.IntervalReported();
    }
    public void Players(float now, IEnumerable<PlayerActivityView> players, HookCounters? observer = null)
    {
        if (!_policy.Enabled) { _players.Reset(); return; }
        _players.Capture(now, players);
        if (!_budget.Reserve(now, "PLAYERS", 1, false)) return;
        var selected = _players.Drain(6);
        foreach (var player in selected)
            DiagnosticLog.Write(_log, "AfkSleepExclusion", DiagnosticEvent.Detail, player.Reason, new (string, object?)[]
            { ("peer", player.Peer), ("session", player.Session), ("state_revision", player.StateRevision), ("known", player.Known), ("alive", player.Alive), ("in_bed", player.InBed), ("inferred_idle", player.Idle), ("idle_observed_s", player.IdleSeconds),
              ("last_observed_signal", player.LastSignal), ("grace_remaining_s_at_sample", player.GraceRemaining), ("state_sample_age_s", System.Math.Max(0, now - player.ObservedAt)), ("recovery", player.Recovery), ("transitions_observed_interval", _players.TransitionsInterval), ("pending_changed_samples_now", _players.Pending), ("pending_states_replaced_interval", _players.ReplacedPendingInterval), ("routed_observer_counts_total", observer?.Format() ?? "not_observed") });
    }
    private long TakeUnchanged() { long result = _unchangedNative; _unchangedNative = 0; return result; }
    public void Native(NativeSleepObservation evidence)
    {
        if (!_policy.Enabled) return;
        string key = evidence.Gate + ":" + evidence.Outcome;
        if (_lastNativeKey == key) { _unchangedNative++; return; }
        if (!_budget.Reserve(evidence.ObservedAt, "NATIVE", 6)) return;
        _lastNativeKey = key;
        DiagnosticLog.Write(_log, "AfkSleepExclusion", DiagnosticEvent.Result, "Native sleep update observed", new (string, object?)[]
        { ("gate", evidence.Gate), ("outcome", evidence.Outcome), ("server", evidence.Server), ("sleeping_before", evidence.Sleeping), ("sleeping_after", evidence.SleepingAfter), ("server_game_time_before_s", evidence.GameTimeBefore), ("server_game_time_after_s", evidence.GameTimeAfter), ("skipping_before", evidence.SkippingBefore), ("skipping_after", evidence.SkippingAfter), ("afternoon", evidence.Afternoon), ("night", evidence.Night), ("cooldown_remaining_s", evidence.CooldownRemaining), ("native_exception", evidence.Failed), ("cause", "unknown"), ("clients_acknowledged", "unobservable") });
    }
}
