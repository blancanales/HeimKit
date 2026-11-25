using System;
using HeimKit.Core.Control.Models;
using HeimKit.Core.Scheduling;

namespace HeimKit.Core.Control.Server;

internal sealed class PendingChange : IDisposable
{
    private IModuleIdle? _gate;
    private readonly float _deadline;

    internal PendingChange(IModuleIdle gate, float now)
    {
        _gate = gate;
        _deadline = now + 60;
        try { gate.HoldNewWork(true); }
        catch { Dispose(); throw; }
    }

    internal SetResult Poll(float now, bool available, Func<SetResult> apply)
    {
        if (_gate == null) return SetResult.Unavailable;
        if (available && now < _deadline && !_gate.Idle) return SetResult.Pending;
        try
        {
            if (!available) return SetResult.Unavailable;
            return now >= _deadline ? SetResult.Busy : apply();
        }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        var gate = _gate;
        _gate = null;
        gate?.HoldNewWork(false);
    }
}
