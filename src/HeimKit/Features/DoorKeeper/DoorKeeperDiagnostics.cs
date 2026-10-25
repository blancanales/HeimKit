using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.DoorKeeper;
internal sealed class DoorKeeperDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly ProcessingTiming _timing;
    public System.IDisposable Measure(float now, System.Func<int> scope) => _timing.Measure(now, scope);
    private readonly DebugLoggingPolicy _policy;
    private readonly ScheduleToken _next = new();
    private long _last;
    private int _scope = -1;
    private readonly ScheduleToken _nextScan = new();
    private (int Doors, int Delayed, int Occupied, bool Ready, string? Reason)? _lastScan;
    public DoorKeeperDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; ConfigureDiagnostics(() => policy.Enabled, log, "DoorKeeper"); _timing = new(log, "DoorKeeper", policy); }
    public void Scan(float now, int records, int doors, int delayed, int occupied, bool ready, string? reason = null, ZDOID? blocking = null)
    {
        var state = (doors, delayed, occupied, ready, reason);
        if (!DiagnosticsEnabled || records == 0 || !_nextScan.Ready(now) || _lastScan == state) return;
        _nextScan.Postpone(now, 60); _lastScan = state;
        if (DiagnosticLog.DebugAvailable(_log, "DoorKeeper")) DiagnosticLog.Write(_log, "DoorKeeper", CoreLogTopics.Scope, "Evaluated door eligibility this pass.", new (string, object?)[] { ("records", records), ("doors", doors), ("delay_wait", delayed), ("occupied", occupied), ("context_ready", ready), ("context_reason", reason), ("blocking_object", blocking) }, LogLevel.Debug);
    }
    public void Result(float now, int count, long total, int scope)
    {
        if (!DiagnosticsEnabled || scope == 0 && total == _last || scope == _scope && total == _last || !_next.Ready(now)) return;
        _next.Postpone(now, 60); _last = total; _scope = scope;
        if (DiagnosticLog.DebugAvailable(_log, "DoorKeeper")) DiagnosticLog.Write(_log, "DoorKeeper", CoreLogTopics.Scope, "Processing scope or results changed this pass.", new (string, object?)[] { ("scope", scope), ("results", count), ("lifetime_results", total) }, LogLevel.Debug);
    }
}
