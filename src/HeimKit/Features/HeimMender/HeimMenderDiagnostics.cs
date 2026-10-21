using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
namespace HeimKit.Features.HeimMender;
internal sealed class HeimMenderDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly ProcessingTiming _timing;
    public System.IDisposable Measure(float now, System.Func<int> scope) => _timing.Measure(now, scope);
    private readonly DebugLoggingPolicy _policy;
    private readonly DiagnosticBudget _budget = new();
    public HeimMenderDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; _timing = new(log, "HeimMender", policy); }
    public void Cycle(float now, string kind, int observed, int damaged, int quiet, int threats, int owners, int selected)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "cycles", 12)) return;
        DiagnosticLog.Write(_log, "HeimMender", DiagnosticEvent.Summary, "Repair selection completed.",
            new (string, object?)[] { ("kind", kind), ("observations_now", observed), ("damaged_pass", damaged), ("quiet_pass", quiet),
                ("threat_pass", threats), ("owner_unavailable_pass", owners), ("selected_pass", selected), ("omitted", _budget.TakeOmitted()) }, LogLevel.Debug);
    }
    public void Request(float now, ZDO record, float health, float maximum)
    {
        if (!_policy.Enabled || !_budget.Reserve(now, "objects", 16)) return;
        DiagnosticLog.Write(_log, "HeimMender", DiagnosticEvent.Action, "Sent native owner repair request.",
            new (string, object?)[] { ("zdo", record.m_uid), ("prefab", record.GetPrefab()), ("owner", record.GetOwner()), ("health", health), ("maximum", maximum) }, LogLevel.Debug);
    }
    public void Finished(float now, string kind, int sent, int confirmed, int unconfirmed)
    {
        if (confirmed > 0) DiagnosticLog.Write(_log, "HeimMender", DiagnosticEvent.Result, "Confirmed " + confirmed + " repairs in the " + kind + " batch.");
        if (!_policy.Enabled || !_budget.Reserve(now, "completed", 12)) return;
        DiagnosticLog.Write(_log, "HeimMender", DiagnosticEvent.Result, "Repair batch completed.",
            new (string, object?)[] { ("kind", kind), ("requests_pass", sent), ("confirmed_pass", confirmed), ("unconfirmed_pass", unconfirmed) }, LogLevel.Debug);
    }
}
