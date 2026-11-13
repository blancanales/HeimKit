using System.Collections.Generic;
using HeimKit.Core.World;
namespace HeimKit.Features.StationSupply;

// Bounded recent manual intent; no inventory or world-record snapshots are retained.
internal sealed class StationSupplyManualAccess
{
    internal const float GraceSeconds = 10;
    private readonly Dictionary<ZDOID, float> _signals = new();
    private float _last = float.NegativeInfinity, _global = float.NegativeInfinity;
    private bool Clock(float now)
    {
        if (!PlayerWorldScope.Finite(now)) return false;
        if (now < _last) { _signals.Clear(); _global = now; }
        _last = now; return true;
    }
    internal void Observe(ZDOID id, float now)
    {
        if (!Clock(now) || id.IsNone()) return;
        if (_signals.Count >= 256 && !_signals.ContainsKey(id)) { _signals.Clear(); _global = now; }
        _signals[id] = now;
    }
    internal bool Available(ZDOID id, float now, out float remaining)
    {
        remaining = GraceSeconds;
        if (!Clock(now)) return false;
        float last = _global;
        if (_signals.TryGetValue(id, out float observed) && observed > last) last = observed;
        remaining = GraceSeconds - (now - last);
        if (remaining > 0) return false;
        _signals.Remove(id); remaining = 0; return true;
    }
    internal void Clear() { _signals.Clear(); _last = _global = float.NegativeInfinity; }
}
