using System;
namespace HeimKit.Features.AFKNightWatch;

public sealed class AFKNightWatchSleepDelay
{
    private float? _metAt;
    private float? _lastAt;
    public float Remaining { get; private set; }
    public bool Allow(float now, bool met, bool everyoneInBed, float delaySeconds)
    {
        if (!met || float.IsNaN(now) || float.IsInfinity(now)) { Reset(); return false; }
        if (everyoneInBed) { Reset(); return true; }
        if (_lastAt.HasValue && now < _lastAt.Value) Reset();
        _lastAt = now;
        _metAt ??= now;
        Remaining = Math.Max(0, delaySeconds - (now - _metAt.Value));
        return Remaining <= 0;
    }
    public void Reset() { _metAt = null; _lastAt = null; Remaining = 0; }
}
