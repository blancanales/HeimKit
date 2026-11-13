using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HeimKit.Core.Scheduling;

internal sealed class ModuleScheduler
{
    internal static ModuleScheduler Current { get; } = new();
    private sealed class Schedule { internal float Next, Interval, Phase; internal bool Started, Waiting, Cancelled; internal long LastFrame; }
    private readonly Dictionary<(object Owner, string Work), Schedule> _work = new();
    private readonly List<(object, string)> _remove = new();
    private int _phase;
    private readonly Func<long> _clock;
    private readonly Queue<Schedule> _waiting = new();
    private long _frame, _frameStart;
    private bool _inFrame;
    private float _now, _lastAdmission = float.NegativeInfinity;
    private bool _forced;
    internal CombatWindow Combat { get; } = new();
    internal bool CombatActive => Combat.Active(_now);
    internal long CombatDeferrals { get; private set; }
    internal long Deferrals { get; private set; }
    internal void ObserveTime(float now) => _now = now;
    internal ModuleScheduler(Func<long>? clock = null) => _clock = clock ?? Stopwatch.GetTimestamp;
    internal bool HasBudget => !CombatActive && (!_inFrame || (_clock() - _frameStart) * 1000d / Stopwatch.Frequency < 4);
    internal void BeginFrame(float now = 0)
    {
        if (!_inFrame) _lastAdmission = now;
        _now = now; _forced = false; _inFrame = true; _frame++; _frameStart = _clock(); PruneWaiting();
    }
    private void PruneWaiting()
    {
        while (_waiting.Count > 0 && (_waiting.Peek().Cancelled || _frame - _waiting.Peek().LastFrame > 2))
            _waiting.Dequeue().Waiting = false;
    }
    internal bool Due(object owner, string work, float now, float interval, bool cooperative = false)
    {
        var key = (owner, work);
        if (!_work.TryGetValue(key, out var item))
        { item = new Schedule { Next = now, Interval = interval, Phase = (_phase++ % 10) * 0.025f }; _work.Add(key, item); }
        if (!item.Started) { item.Started = true; item.Next = now + Math.Min(item.Phase, interval * .1f); }
        if (item.Interval != interval) { item.Interval = interval; item.Next = now + interval; }
        if (now < item.Next) return false;
        // Battle pressure is a hard admission stop, including starvation recovery.
        // Do not advance Next: resume one ordinary due unit after pressure clears.
        if (cooperative && Combat.Active(now)) { Deferrals++; CombatDeferrals++; return false; }
        if (cooperative && _inFrame)
        {
            _now = now;
            item.LastFrame = _frame; PruneWaiting();
            bool first = _waiting.Count == 0 || ReferenceEquals(_waiting.Peek(), item);
            bool progress = first && !_forced && (now < _lastAdmission || now - _lastAdmission >= .25f);
            if (!first || !HasBudget && !progress)
            {
                if (!item.Waiting) { item.Waiting = true; _waiting.Enqueue(item); }
                Deferrals++; return false;
            }
            if (!HasBudget) _forced = true;
            _lastAdmission = now;
            if (item.Waiting) { _waiting.Dequeue(); item.Waiting = false; }
        }
        item.Next = now + interval; return true;
    }
    internal void Forget(object owner)
    {
        _remove.Clear(); foreach (var key in _work.Keys) if (ReferenceEquals(key.Owner, owner)) _remove.Add(key);
        foreach (var key in _remove) { _work[key].Cancelled = true; _work.Remove(key); } _remove.Clear(); PruneWaiting();
    }
    internal void Clear() { _work.Clear(); _waiting.Clear(); _phase = 0; _frame = _frameStart = Deferrals = CombatDeferrals = 0; _inFrame = false; _forced = false; _lastAdmission = float.NegativeInfinity; Combat.Clear(); }
}

// Non-gameplay polling and per-target cadences share the same clock contract.
internal sealed class ScheduleToken
{
    private float _next;
    internal bool Ready(float now) => now >= _next;
    internal void Postpone(float now, float interval) => _next = now + interval;
    // Bring a later deadline forward; an earlier one is left alone.
    internal void Hasten(float now, float interval) { if (now + interval < _next) _next = now + interval; }
}
