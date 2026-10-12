using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Shared.Players.Models;
namespace HeimKit.Shared.Players;
// One shared stream is enabled only by a registered consumer's effective policy.
public sealed class PlayerActivityDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly Func<bool> _enabled;
    private readonly ActivitySamples _samples = new();
    private readonly DiagnosticBudget _budget = new();
    public PlayerActivityDiagnostics(ManualLogSource log, Func<bool> enabled) { _log = log; _enabled = enabled; }
    public void Players(float now, IEnumerable<PlayerActivityView> players)
    {
        if (!_enabled() || !DiagnosticLog.ServerActivity.Allowed) { Reset(); return; }
        _samples.Capture(now, players);
        if (!_budget.Reserve(now, "PLAYERS", 1, false)) return;
        var changed = _samples.Drain(6);
        if (changed.Count == 0) return;
        DiagnosticLog.Write(_log, "PlayerActivity", DiagnosticEvent.Summary, "Player activity observations changed", new (string, object?)[] {
            ("transitions_interval", _samples.TransitionsInterval), ("pending_states_replaced_interval", _samples.ReplacedPendingInterval),
            ("pending_states_now", _samples.Pending), ("actor_samples_interval", changed.Count) }, LogLevel.Debug);
        _samples.IntervalReported();
        foreach (var p in changed)
            DiagnosticLog.Write(_log, "PlayerActivity", DiagnosticEvent.Detail, p.Reason, new (string, object?)[]
            { ("peer", p.Peer), ("session", p.Session), ("state_revision", p.StateRevision), ("known", p.Known), ("alive", p.Alive), ("in_bed", p.InBed), ("inferred_idle", p.Idle), ("idle_observed_s", p.IdleSeconds), ("last_observed_signal", p.LastSignal), ("grace_remaining_s_at_sample", p.GraceRemaining), ("state_sample_age_s", Math.Max(0, now - p.ObservedAt)), ("recovery", p.Recovery) }, LogLevel.Debug);
    }
    public void Reset() { _samples.Reset(); }
}
