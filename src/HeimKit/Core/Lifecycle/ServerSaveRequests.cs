using System;
using System.Diagnostics;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using Splatform;

namespace HeimKit.Core.Lifecycle;

// Audited Windows dedicated-server compatibility and save elapsed observations.
// Authentication, disk checks, native save execution and exceptions stay native.
internal sealed class ServerSaveRequests : IDisposable
{
    private readonly Harmony _hooks = new("heimkit.server_save_requests");
    private readonly ManualLogSource _log;
    private static ServerSaveRequests? _current;
    private bool _announced;
    private long _nextWarning;
    internal ServerSaveRequests(ManualLogSource log)
    {
        _log = log;
        try
        {
            _hooks.Patch(NativeAccess.Target(typeof(ZNet), "HardSaveBlock"), prefix: new HarmonyMethod(typeof(ServerSaveRequests), nameof(BeforeGuard)));
            _hooks.Patch(NativeAccess.Target(typeof(ZNet), "RPC_Save", typeof(ZRpc)),
                prefix: new HarmonyMethod(typeof(ServerSaveRequests), nameof(Begin)), finalizer: new HarmonyMethod(typeof(ServerSaveRequests), nameof(RequestEnded)));
            _hooks.Patch(NativeAccess.Target(typeof(ZNet), "SaveWorld", typeof(bool)),
                prefix: new HarmonyMethod(typeof(ServerSaveRequests), nameof(Begin)), finalizer: new HarmonyMethod(typeof(ServerSaveRequests), nameof(SaveEnded)));
            _current = this;
        }
        catch { _hooks.UnpatchSelf(); throw; }
    }
    internal static bool NeedsFallback(bool server, bool dedicated, bool gameReady, bool platformAvailable) => server && dedicated && gameReady && !platformAvailable;
    private static bool BeforeGuard(ZNet __instance, ref bool __result)
    {
        var current = _current;
        if (current == null || !NeedsFallback(__instance.IsServer(), __instance.IsDedicated(), Game.instance != null, PlatformManager.DistributionPlatform != null)) return true;
        // The audited guard only rate-limits Nintendo. Missing platform metadata
        // on this Windows server must have the same false result as Windows.
        __result = false;
        ServerPerformance.SaveGuardFallbacks.Add(0);
        if (!current._announced)
        {
            current._announced = true;
            try { DiagnosticLog.Write(current._log, "HeimKit", "Applied the Windows dedicated-server save guard fallback because native platform metadata is unavailable."); }
            catch { /* Optional observation cannot abort a native save request. */ }
        }
        return false;
    }
    private static void Begin(out long __state) => __state = Stopwatch.GetTimestamp();
    private static Exception? RequestEnded(Exception? __exception, long __state)
    {
        try
        {
            if (__state != 0) PerformanceStages.Record("HeimKit", "native_save_request", ServerPerformance.Milliseconds(Stopwatch.GetTimestamp() - __state));
            if (__exception != null) _current?.Warn(__exception);
        }
        catch { /* Preserve the native outcome if a diagnostics listener fails. */ }
        return __exception;
    }
    private static Exception? SaveEnded(Exception? __exception, long __state)
    {
        try { if (__state != 0) PerformanceStages.Record("HeimKit", "native_save_prepare", ServerPerformance.Milliseconds(Stopwatch.GetTimestamp() - __state)); }
        catch { /* Preserve the native outcome if observation fails. */ }
        return __exception;
    }
    private void Warn(Exception error)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextWarning) return;
        _nextWarning = now + Stopwatch.Frequency * 300;
        DiagnosticLog.Write(_log, "HeimKit", "Native save request failed with " + error.GetType().Name + "; completion is unconfirmed. The native exception remains visible.", LogLevel.Warning);
    }
    public void Dispose() { if (ReferenceEquals(_current, this)) _current = null; _hooks.UnpatchSelf(); }
}
