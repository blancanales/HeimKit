using System;

namespace HeimKit.Core.Scheduling;

internal sealed class CombatWindow
{
    private float _last = float.NegativeInfinity, _until = float.NegativeInfinity, _bossUntil = float.NegativeInfinity;
    internal bool Boss { get; private set; }
    internal bool Raid { get; private set; }
    internal bool Active(float now) { if (now < _last) Clear(); if (now >= _bossUntil) Boss = false; return Raid || now < _until; }
    internal void SetRaid(bool active) => Raid = active;
    internal void Observe(float now, bool boss)
    {
        if (!Active(now)) Boss = false;
        _last = now; Boss |= boss; if (boss) _bossUntil = now + 20;
        _until = Math.Max(_until, now + (boss ? 20 : 8));
    }
    internal void Clear() { _last = _until = _bossUntil = float.NegativeInfinity; Boss = Raid = false; }
}
