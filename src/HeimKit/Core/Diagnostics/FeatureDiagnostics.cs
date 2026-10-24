using System;
using BepInEx.Logging;

namespace HeimKit.Core.Diagnostics;

// Shared optional-output gate and stream budget. The feature supplies messages;
// snapshots and their dictionaries are created only after SummaryReady succeeds.
public abstract class FeatureDiagnostics
{
    private Func<bool> _enabled = () => false;
    private ManualLogSource? _logger;
    private string _name = "";
    protected readonly DiagnosticBudget _budget = new();
    protected void ConfigureDiagnostics(Func<bool> enabled, ManualLogSource? logger = null, string name = "")
    { _enabled = enabled; _logger = logger; _name = name; }
    protected bool DiagnosticsEnabled => _enabled() && DiagnosticLog.ServerActivity.Allowed &&
        (_logger == null || DiagnosticLog.DebugAvailable(_logger, _name));
    public bool VerboseEnabled => DiagnosticsEnabled && DiagnosticLog.VerboseConfigured;
    public bool VerboseAfterNormalEnabled => VerboseEnabled && (_logger == null || DiagnosticLog.DebugAvailable(_logger, _name, true, 2));
    public bool OptionalEnabled => DiagnosticsEnabled;
    public bool SummaryReady(float now) => DiagnosticsEnabled && _budget.Available(now, "summary", 1);
    protected bool SummaryDue(float now) => DiagnosticsEnabled && _budget.Reserve(now, "summary", 1, false);
    protected bool RecordDue(float now, string stream, int limit) => DiagnosticsEnabled && _budget.Reserve(now, stream, limit);
}
