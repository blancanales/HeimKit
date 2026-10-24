using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.ForestRenewal;
internal sealed class ForestRenewalDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly ProcessingTiming _timing;
    public System.IDisposable Measure(float now, System.Func<int> scope) => _timing.Measure(now, scope);
    private readonly DebugLoggingPolicy _policy;
    private readonly ScheduleToken _next = new();
    private readonly ScheduleToken _nextWrite = new();
    private readonly System.Collections.Generic.Dictionary<string, (float Next, int Calls, double Total, double Maximum)> _stages = new();
    private long _last;
    private int _scope = -1;
    private readonly System.Collections.Generic.Dictionary<(ZDOID Object, string Reason, string? Detail), float> _decisions = new();
    public ForestRenewalDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; ConfigureDiagnostics(() => policy.Enabled, log, "ForestRenewal"); _timing = new(log, "ForestRenewal", policy); }
    public void Decision(float now, ZDOID source, string reason, ZDO? blocker = null, string? detail = null)
    {
        if (!DiagnosticsEnabled) return;
        var key = (blocker?.m_uid ?? source, reason, detail);
        if (_decisions.TryGetValue(key, out float previous) && now >= previous && now - previous < 300) return;
        if (!_budget.Reserve(now, "decisions", 16)) return;
        // Diagnostics must not grow with an unbounded history of world objects.
        if (_decisions.Count >= 4096 && !_decisions.ContainsKey(key)) _decisions.Clear();
        _decisions[key] = now;
        if (DiagnosticLog.DebugAvailable(_log, "ForestRenewal")) DiagnosticLog.Write(_log, "ForestRenewal", ForestRenewalLogTopics.Renewal, $"Sapling renewal for {source}: {reason.Replace('_', ' ')}.", new (string, object?)[] { ("source", source), ("reason", reason), ("blocking_object", blocker?.m_uid), ("blocking_prefab", blocker?.GetPrefab()), ("blocking_reason", detail) }, LogLevel.Debug);
    }
    public void LedgerWrite(float now, double duration)
    {
        if (!DiagnosticsEnabled || !_nextWrite.Ready(now)) return;
        _nextWrite.Postpone(now, 60);
        if (DiagnosticLog.DebugAvailable(_log, "ForestRenewal")) DiagnosticLog.Write(_log, "ForestRenewal", ForestRenewalLogTopics.Renewal, "Durable renewal ledger write completed on background worker.", new (string, object?)[] { ("background_io_ms", duration) }, LogLevel.Debug);
    }
    public long BeginStage() => DiagnosticsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    public void EndStage(float now, string stage, long started)
    {
        if (started == 0 || !DiagnosticsEnabled) return;
        if (stage != "ledger_snapshot" && stage != "terrain_context" && stage != "world_mutation" && stage != "scope_refresh" && stage != "structure_exclusion")
            throw new System.ArgumentException("Unknown renewal timing stage.", nameof(stage));
        double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        _stages.TryGetValue(stage, out var data);
        data.Calls++; data.Total += elapsed; data.Maximum = System.Math.Max(data.Maximum, elapsed);
        if (now < data.Next) { _stages[stage] = data; return; }
        _stages[stage] = (now + 60, 0, 0, 0);
        if (DiagnosticLog.DebugAvailable(_log, "ForestRenewal")) DiagnosticLog.Write(_log, "ForestRenewal", CoreLogTopics.Timing, "Measured renewal stage processing in the last minute.", new (string, object?)[] { ("stage", stage), ("calls", data.Calls), ("duration_avg_ms", data.Total / data.Calls), ("duration_max_ms", data.Maximum) }, LogLevel.Debug);
    }
    public void Result(float now, int count, long total, int scope)
    {
        if (!DiagnosticsEnabled || scope == 0 && total == _last || scope == _scope && total == _last || !_next.Ready(now)) return;
        _next.Postpone(now, 60); _last = total; _scope = scope;
        if (DiagnosticLog.DebugAvailable(_log, "ForestRenewal")) DiagnosticLog.Write(_log, "ForestRenewal", CoreLogTopics.Scope, "Processing scope or results changed this pass.", new (string, object?)[] { ("scope", scope), ("results", count), ("lifetime_results", total) }, LogLevel.Debug);
    }
}
