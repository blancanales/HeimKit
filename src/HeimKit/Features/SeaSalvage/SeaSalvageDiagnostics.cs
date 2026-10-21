using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
namespace HeimKit.Features.SeaSalvage;
internal sealed class SeaSalvageDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly ProcessingTiming _timing;
    public System.IDisposable Measure(float now, System.Func<int> scope) => _timing.Measure(now, scope);
    private readonly DebugLoggingPolicy _policy;
    private float _next;
    private long _last;
    private int _scope = -1;
    private readonly DiagnosticBudget _budget = new();
    private float _nextScan;
    public SeaSalvageDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; _timing = new(log, "SeaSalvage", policy); }
    public void Scan(float now, int records, int candidates, int owners, int players, int settling, int terrain, int depth, int invalid)
    {
        if (!_policy.Enabled || candidates == 0 || now < _nextScan) return;
        _nextScan = now + 60;
        DiagnosticLog.Write(_log, "SeaSalvage", DiagnosticEvent.Summary, "Sunken-item eligibility scope.",
            new (string, object?)[] { ("records_now", records), ("candidates_pass", candidates), ("client_owned_pass", owners), ("player_near_pass", players),
                ("settling_pass", settling), ("terrain_wait_pass", terrain), ("depth_excluded_pass", depth), ("invalid_items_pass", invalid) }, LogLevel.Debug);
    }
    public void Transfer(float now, ZDOID source, ZDOID cargo)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "transfers", 16)) return;
        DiagnosticLog.Write(_log, "SeaSalvage", DiagnosticEvent.Result, "Recorded source inventory in cargo before source destruction.",
            new (string, object?)[] { ("source", source), ("cargo", cargo) }, LogLevel.Debug);
    }
    public void Result(float now, int count, long total, int scope)
    {
        if (!_policy.Enabled || scope == 0 && total == _last || scope == _scope && total == _last || now < _next) return;
        _next = now + 60; _last = total; _scope = scope;
        DiagnosticLog.Write(_log, "SeaSalvage", DiagnosticEvent.Summary, "Processing scope or results changed.",
            new (string, object?)[] { ("scope_now", scope), ("results_pass", count), ("results_total", total) }, LogLevel.Debug);
    }
}
