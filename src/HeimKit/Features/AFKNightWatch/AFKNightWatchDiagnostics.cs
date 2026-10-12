using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
using UnityEngine;
using HeimKit.Features.AFKNightWatch.Models;
namespace HeimKit.Features.AFKNightWatch;
public sealed class AFKNightWatchDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly DebugLoggingPolicy _policy;
    private readonly DiagnosticBudget _budget = new();
    private readonly SummaryCadence _summary = new();
    public bool Enabled => _policy.Enabled;
    private float _lastWarning = float.NegativeInfinity;
    private string? _lastNativeKey;
    private long _unchangedNative;
    public AFKNightWatchDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; }
    private void Write(DiagnosticEvent category, string message, IEnumerable<(string Key, object? Value)>? fields = null, LogLevel level = LogLevel.Debug)
        => DiagnosticLog.Write(_log, "AFKNightWatch", category, message, fields, level);
    public void Report(float now, Func<NightWatchStatus> snapshot)
    {
        if (!Enabled) { _lastNativeKey = null; _summary.Reset(); return; }
        if (!_summary.CanPoll(now)) return;
        var state = snapshot();
        if (state.Players == 0 || state.Vote == null) { _summary.Reset(); return; }
        var vote = state.Vote; var outcome = state.SleepOutcome;
        string key = $"{state.Players}:{vote.Participants}:{vote.Sleepers}:{vote.Required}:{vote.ExcludedIdle}:{vote.Met}:{vote.Unknown}:{outcome?.Outcome}:{state.Outcomes}";
        if (!_summary.ShouldEmit(now, key)) return;
        if (!_summary.Changed)
        {
            Write(DiagnosticEvent.Summary, "Heartbeat: last relevant observation unchanged", new (string, object?)[] { ("observation_age_s", state.VoteAge), ("omitted_messages_interval", _budget.TakeOmitted()) });
            return;
        }
        Write(DiagnosticEvent.Summary, vote.Unknown ? "Unknown actor data; native fallback depends on a living sleeper" : vote.Met ? "Sleep quorum met; native time checks still apply" : "Sleep quorum not met", new (string, object?)[] {
            ("majority_enabled", state.MajorityEnabled), ("afk_sleep_exclusion_enabled", state.ExclusionEnabled),
            ("participants_last_vote", vote.Participants), ("sleepers_last_vote", vote.Sleepers), ("required_last_vote", vote.Required),
            ("excluded_idle_last_vote", vote.ExcludedIdle), ("quorum_met_last_vote", vote.Met), ("native_vote_fallback", vote.Unknown && vote.Sleepers > 0),
            ("votes_observed_total", state.Votes), ("vote_age_s", state.VoteAge), ("time_skip", outcome?.Outcome ?? state.Sleep?.Outcome ?? "not_observed"),
            ("last_outcome_age_s", outcome == null ? (float?)null : Math.Max(0, now - outcome.ObservedAt)),
            ("native_probe_age_s", state.Sleep == null ? (float?)null : Math.Max(0, now - state.Sleep.ObservedAt)),
            ("unchanged_native_samples_interval", TakeUnchangedNative()), ("omitted_messages_interval", _budget.TakeOmitted()) });
        if (_budget.Reserve(now, "HOOK_DETAIL", 1, false)) Write(DiagnosticEvent.Detail, "Sleep hook evidence", new (string, object?)[] { ("sleep_hook_counts_total", state.Hooks?.Format() ?? "not_observed") });
    }
    public void Warning(float now, Exception error)
    {
        if (now >= _lastWarning && now - _lastWarning < 300) return;
        _lastWarning = now;
        Write(DiagnosticEvent.Lifecycle, "Diagnostic observation unavailable; native behavior unchanged: " + error.Message, level: LogLevel.Warning);
    }
    private long TakeUnchangedNative() { long result = _unchangedNative; _unchangedNative = 0; return result; }
    public void Native(NativeSleepObservation evidence)
    {
        if (!_policy.Enabled) return;
        string key = evidence.Gate + ":" + evidence.Outcome;
        if (_lastNativeKey == key) { _unchangedNative++; return; }
        if (!_budget.Reserve(evidence.ObservedAt, "NATIVE", 6)) return;
        _lastNativeKey = key;
        Write(DiagnosticEvent.Result, "Native sleep update observed", new (string, object?)[]
        { ("gate", evidence.Gate), ("outcome", evidence.Outcome), ("server", evidence.Server), ("sleeping_before", evidence.Sleeping), ("sleeping_after", evidence.SleepingAfter), ("server_game_time_before_s", evidence.GameTimeBefore), ("server_game_time_after_s", evidence.GameTimeAfter), ("skipping_before", evidence.SkippingBefore), ("skipping_after", evidence.SkippingAfter), ("afternoon", evidence.Afternoon), ("night", evidence.Night), ("cooldown_remaining_s", evidence.CooldownRemaining), ("native_exception", evidence.Failed), ("cause", "unknown"), ("clients_acknowledged", "unobservable") });
    }
}
