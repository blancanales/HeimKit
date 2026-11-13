using System;
using System.Collections.Generic;
using HeimKit.Core.Native;
using HeimKit.Core.World;
namespace HeimKit.Features.StationSupply;

// Lever evidence is conservative and bounded; it never authorizes destruction.
internal sealed class StationSupplyObliterator
{
    private static readonly int Request = "RPC_RequestIncinerate".GetStableHashCode();
    private static readonly int Animate = "RPC_AnimateLever".GetStableHashCode();
    private static readonly int Return = "RPC_AnimateLeverReturn".GetStableHashCode();
    private readonly Dictionary<ZDOID, float> _signals = new();
    private float _started, _overflowedAt = float.NegativeInfinity;
    private readonly float _quietSeconds;
    private static class Accessors
    {
        internal static readonly HarmonyLib.AccessTools.FieldRef<Incinerator, bool> Busy = NativeAccess.Field<Incinerator, bool>("isInUse");
    }
    internal StationSupplyObliterator(float started, float quietSeconds)
    {
        _started = started; _quietSeconds = quietSeconds; _ = Accessors.Busy;
    }
    internal static bool LeverMethod(int method) => method == Request || method == Animate || method == Return;
    internal void Observe(ZDOID target, int method, float now)
    {
        if (target.IsNone() || !LeverMethod(method) || !PlayerWorldScope.Finite(now)) return;
        if (_signals.Count >= 256 && !_signals.ContainsKey(target)) { _signals.Clear(); _overflowedAt = now; }
        _signals[target] = now;
    }
    internal bool Quiet(ZDOID target, float now)
        => Quiet(target, now, out _, out _);
    internal bool Quiet(ZDOID target, float now, out string reason, out float remaining)
    {
        reason = "obliterator_invalid_clock"; remaining = 0;
        if (!PlayerWorldScope.Finite(now)) return false;
        if (now < _started) { _started = now; _signals.Clear(); _overflowedAt = now; reason = "obliterator_clock_reset"; remaining = _quietSeconds; return false; }
        if (now - _started < _quietSeconds) { reason = "obliterator_startup_quiet"; remaining = _quietSeconds - (now - _started); return false; }
        if (now - _overflowedAt < _quietSeconds) { reason = "obliterator_signal_overflow_quiet"; remaining = _quietSeconds - (now - _overflowedAt); return false; }
        if (_signals.TryGetValue(target, out float last))
        {
            if (now - last < _quietSeconds) { reason = "obliterator_lever_quiet"; remaining = _quietSeconds - (now - last); return false; }
            _signals.Remove(target);
        }
        reason = "obliterator_ready"; return true;
    }
    internal static bool LocallyBusy(Incinerator? instance) => instance != null && Accessors.Busy(instance);
}
