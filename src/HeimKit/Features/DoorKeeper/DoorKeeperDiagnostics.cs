using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
namespace HeimKit.Features.DoorKeeper;
internal sealed class DoorKeeperDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly ProcessingTiming _timing;
    public System.IDisposable Measure(float now, System.Func<int> scope) => _timing.Measure(now, scope);
    private readonly DebugLoggingPolicy _policy;
    private float _next;
    private long _last;
    private int _scope = -1;
    private float _nextScan;
    private (int Doors, int Delayed, int Occupied, bool Ready, string? Reason)? _lastScan;
    public DoorKeeperDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; _timing = new(log, "DoorKeeper", policy); }
    public void Scan(float now, int records, int doors, int delayed, int occupied, bool ready, string? reason = null, ZDOID? blocking = null)
    {
        var state = (doors, delayed, occupied, ready, reason);
        if (!_policy.Enabled || records == 0 || now < _nextScan || _lastScan == state) return;
        _nextScan = now + 60; _lastScan = state;
        DiagnosticLog.Write(_log, "DoorKeeper", DiagnosticEvent.Summary, "Door eligibility scope.",
            new (string, object?)[] { ("records_now", records), ("doors_now", doors), ("delay_wait_pass", delayed), ("occupied_pass", occupied), ("context_ready", ready),
                ("context_reason", reason), ("blocking_object", blocking) }, LogLevel.Debug);
    }
    public void Result(float now, int count, long total, int scope)
    {
        if (!_policy.Enabled || scope == 0 && total == _last || scope == _scope && total == _last || now < _next) return;
        _next = now + 60; _last = total; _scope = scope;
        DiagnosticLog.Write(_log, "DoorKeeper", DiagnosticEvent.Summary, "Processing scope or results changed.",
            new (string, object?)[] { ("scope_now", scope), ("results_pass", count), ("results_total", total) }, LogLevel.Debug);
    }
}
