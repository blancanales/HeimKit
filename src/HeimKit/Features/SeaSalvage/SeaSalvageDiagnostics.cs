using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Features.SeaSalvage;
internal sealed class SeaSalvageDiagnostics : FeatureDiagnostics
{
    private readonly ManualLogSource _log;
    private readonly ProcessingTiming _timing;
    public System.IDisposable Measure(float now, System.Func<int> scope) => _timing.Measure(now, scope);
    private readonly DebugLoggingPolicy _policy;
    private readonly ScheduleToken _next = new();
    private long _last;
    private int _scope = -1;
    private readonly ScheduleToken _nextScan = new();
    public SeaSalvageDiagnostics(ManualLogSource log, DebugLoggingPolicy policy) { _log = log; _policy = policy; ConfigureDiagnostics(() => policy.Enabled, log, "SeaSalvage"); _timing = new(log, "SeaSalvage", policy); }
    public void Scan(float now, int records, int candidates, int owners, int players, int settling, int terrain, int depth, int invalid)
    {
        if (!DiagnosticsEnabled || candidates == 0 || !_nextScan.Ready(now)) return;
        _nextScan.Postpone(now, 60);
        if (DiagnosticLog.DebugAvailable(_log, "SeaSalvage")) DiagnosticLog.Write(_log, "SeaSalvage", CoreLogTopics.Scope, $"Evaluated {candidates} sunken-item candidates this pass.", new (string, object?)[] { ("records", records), ("candidates", candidates), ("client_owned", owners), ("player_near", players), ("settling", settling), ("terrain_wait", terrain), ("depth_excluded", depth), ("invalid_items", invalid) }, LogLevel.Debug);
    }
    public void Transfer(float now, ZDOID source, ZDOID cargo)
    {
        if (!DiagnosticsEnabled || !_budget.Reserve(now, "transfers", 16)) return;
        if (DiagnosticLog.DebugAvailable(_log, "SeaSalvage")) DiagnosticLog.Write(_log, "SeaSalvage", SeaSalvageLogTopics.Salvage, "Recorded source inventory in cargo before source destruction.", new (string, object?)[] { ("source", source), ("cargo", cargo) }, LogLevel.Debug);
    }
    public void Result(float now, int count, long total, int scope)
    {
        if (!DiagnosticsEnabled || scope == 0 && total == _last || scope == _scope && total == _last || !_next.Ready(now)) return;
        _next.Postpone(now, 60); _last = total; _scope = scope;
        if (DiagnosticLog.DebugAvailable(_log, "SeaSalvage")) DiagnosticLog.Write(_log, "SeaSalvage", CoreLogTopics.Scope, "Processing scope or results changed this pass.", new (string, object?)[] { ("scope", scope), ("results", count), ("lifetime_results", total) }, LogLevel.Debug);
    }
}
