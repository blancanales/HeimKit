using System;
using System.Collections.Generic;

namespace HeimKit.Core.Scheduling;

internal sealed class ModuleScheduler
{
    internal static ModuleScheduler Current { get; } = new();
    private sealed class Schedule { internal float Next, Interval, Phase; internal bool Started; }
    private readonly Dictionary<(object Owner, string Work), Schedule> _work = new();
    private readonly List<(object, string)> _remove = new();
    private int _phase;
    internal bool Due(object owner, string work, float now, float interval)
    {
        var key = (owner, work);
        if (!_work.TryGetValue(key, out var item))
        { item = new Schedule { Next = now, Interval = interval, Phase = (_phase++ % 10) * 0.025f }; _work.Add(key, item); }
        if (!item.Started) { item.Started = true; item.Next = now + Math.Min(item.Phase, interval * .1f); }
        if (item.Interval != interval) { item.Interval = interval; item.Next = now + interval; }
        if (now < item.Next) return false;
        item.Next = now + interval; return true;
    }
    internal void Forget(object owner)
    {
        _remove.Clear(); foreach (var key in _work.Keys) if (ReferenceEquals(key.Owner, owner)) _remove.Add(key);
        foreach (var key in _remove) _work.Remove(key); _remove.Clear();
    }
    internal void Clear() { _work.Clear(); _phase = 0; }
}

// Non-gameplay polling and per-target cadences share the same clock contract.
internal sealed class ScheduleToken
{
    private float _next;
    internal bool Ready(float now) => now >= _next;
    internal void Postpone(float now, float interval) => _next = now + interval;
}
