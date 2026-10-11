using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
namespace HeimKit.Features.MajoritySleep;
public sealed class MajoritySleepDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly DebugLoggingPolicy _policy;
    private readonly DiagnosticBudget _budget = new();
    public bool Enabled => _policy.Enabled;
    private string? _lastNativeKey;
    private long _unchangedNative;
    public MajoritySleepDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; }
    public void Report(float now, SleepDecision? decision, long evaluations, float? voteAge = null, HookCounters? hooks = null, NativeSleepObservation? native = null, NativeSleepObservation? lastOutcome = null)
    {
        if (!_policy.Enabled) { _lastNativeKey = null; _unchangedNative = 0; return; }
        if (!_budget.Reserve(now, "SUMMARY", 1, false)) return;
        DiagnosticLog.Write(_log, "MajoritySleep", DiagnosticEvent.Summary, decision == null ? "Native sleep vote not observed" : decision.Unknown ? decision.Sleepers > 0 ? "Unknown actor data; native vote retained" : "Unknown actor data; no known living bed sleeper" : decision.Met ? "Sleep quorum met; native time checks still apply" : "Sleep quorum not met", new (string, object?)[]
        { ("participants_last_vote", decision?.Participants), ("sleepers_last_vote", decision?.Sleepers), ("required_last_vote", decision?.Required), ("excluded_idle_last_vote", decision?.ExcludedIdle), ("quorum_met_last_vote", decision?.Met), ("native_vote_fallback", decision == null ? (bool?)null : decision.Unknown && decision.Sleepers > 0), ("votes_observed_total", evaluations), ("vote_age_s", voteAge), ("time_skip", lastOutcome?.Outcome ?? native?.Outcome ?? "not_observed"), ("last_outcome_age_s", lastOutcome == null ? (float?)null : System.Math.Max(0, now - lastOutcome.ObservedAt)),
          ("hook_counts_total", hooks?.Format() ?? "not_observed"), ("native_gate_last_update", native?.Gate), ("native_probe_age_s", native == null ? (float?)null : System.Math.Max(0, now - native.ObservedAt)),
          ("afternoon_last_update", native?.Afternoon), ("night_last_update", native?.Night), ("cooldown_remaining_s_last_update", native?.CooldownRemaining),
          ("native_outcome_cause", "unknown"), ("unchanged_native_samples_interval", TakeUnchanged()), ("omitted_messages_interval", _budget.TakeOmitted()) });
    }
    private long TakeUnchanged() { long result = _unchangedNative; _unchangedNative = 0; return result; }
    public void Native(NativeSleepObservation evidence)
    {
        if (!_policy.Enabled) return;
        string key = evidence.Gate + ":" + evidence.Outcome;
        if (_lastNativeKey == key) { _unchangedNative++; return; }
        if (!_budget.Reserve(evidence.ObservedAt, "NATIVE", 6)) return;
        _lastNativeKey = key;
        DiagnosticLog.Write(_log, "MajoritySleep", DiagnosticEvent.Result, "Native sleep update observed", new (string, object?)[]
        { ("gate", evidence.Gate), ("outcome", evidence.Outcome), ("server", evidence.Server), ("sleeping_before", evidence.Sleeping), ("sleeping_after", evidence.SleepingAfter), ("server_game_time_before_s", evidence.GameTimeBefore), ("server_game_time_after_s", evidence.GameTimeAfter), ("skipping_before", evidence.SkippingBefore), ("skipping_after", evidence.SkippingAfter), ("afternoon", evidence.Afternoon), ("night", evidence.Night), ("cooldown_remaining_s", evidence.CooldownRemaining), ("native_exception", evidence.Failed), ("cause", "unknown"), ("clients_acknowledged", "unobservable") });
    }
}
