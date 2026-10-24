using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.HeimMender;
internal sealed class HeimMenderDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly ProcessingTiming _timing;
    public System.IDisposable Measure(float now, System.Func<int> scope) => _timing.Measure(now, scope);
    private readonly DebugLoggingPolicy _policy;
    public HeimMenderDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; ConfigureDiagnostics(() => policy.Enabled, log, "HeimMender"); _timing = new(log, "HeimMender", policy); }
    public void Cycle(float now, string kind, int observed, int damaged, int quiet, int threats, int owners, int selected)
    {
        if (!DiagnosticsEnabled || !_budget.Reserve(now, "cycles", 12)) return;
        if (DiagnosticLog.DebugAvailable(_log, "HeimMender")) DiagnosticLog.Write(_log, "HeimMender", CoreLogTopics.Scope, $"Selected {selected} repair requests for the {kind} batch this pass.", new (string, object?)[] { ("kind", kind), ("observations", observed), ("damaged", damaged), ("quiet", quiet), ("threat", threats), ("owner_unavailable", owners), ("selected", selected), ("omitted", _budget.TakeOmitted()) }, LogLevel.Debug);
    }
    public void Request(float now, ZDO record, float health, float maximum)
    {
        if (!DiagnosticsEnabled || !_budget.Reserve(now, "objects", 16)) return;
        if (DiagnosticLog.DebugAvailable(_log, "HeimMender")) DiagnosticLog.Write(_log, "HeimMender", HeimMenderLogTopics.Repair, "Sent native owner repair request.", new (string, object?)[] { ("zdo", record.m_uid), ("prefab", record.GetPrefab()), ("owner", record.GetOwner()), ("health", health), ("maximum", maximum) }, LogLevel.Debug);
    }
    public void Finished(float now, string kind, int sent, int confirmed, int unconfirmed)
    {
        if (confirmed > 0) DiagnosticLog.Write(_log, "HeimMender", "Confirmed " + confirmed + " repairs in the " + kind + " batch.");
        if (!DiagnosticsEnabled || !_budget.Reserve(now, "completed", 12)) return;
        if (DiagnosticLog.DebugAvailable(_log, "HeimMender")) DiagnosticLog.Write(_log, "HeimMender", HeimMenderLogTopics.Repair, $"Repair batch {kind} finished with {confirmed} confirmations and {unconfirmed} unconfirmed requests.", new (string, object?)[] { ("kind", kind), ("requests", sent), ("confirmed", confirmed), ("unconfirmed", unconfirmed) }, LogLevel.Debug);
    }
}
