using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;

namespace HeimKit.Core.Scheduling;

internal sealed class ModuleHost : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly List<IServerModule> _modules = new();
    private readonly Dictionary<IServerModule, FailureIncident> _failures = new();
    private sealed class FailureIncident
    {
        internal string Stage = "";
        internal Type OuterType = null!, CauseType = null!;
        internal MethodBase? Location;
        internal float Next;
        internal long Suppressed;
    }
    internal ModuleHost(ManualLogSource log) => _log = log;
    internal void Register(Func<IServerModule> create, string name)
    {
        try { _modules.Add(create()); }
        catch (Exception error) { DiagnosticLog.Write(_log, name, CoreLogTopics.Lifecycle, $"Disabled after configuration construction error.", level: LogLevel.Error, error: error); }
    }
    internal void Initialize(string version)
    {
        _failures.Clear();
        foreach (var module in _modules)
            try { module.Initialize(version); }
            catch (Exception error) { Fail(module, error, "startup", 0); }
    }
    internal void Tick(float now)
    {
        foreach (var module in _modules)
            // Shutdown gates gameplay; inactive ticks still serve local reports.
            try
            {
                module.Tick(now);
                if (_failures.Count > 0 && module.IsActive) _failures.Remove(module);
            }
            catch (Exception error) { Fail(module, error, "runtime", now); }
    }
    private void Fail(IServerModule module, Exception error, string stage, float now)
    {
        ModuleCleanup.Run(_log, module.Name, module.Shutdown);
        var cause = error.GetBaseException();
        bool changed = !_failures.TryGetValue(module, out var incident) || incident.Stage != stage ||
            incident.OuterType != error.GetType() || incident.CauseType != cause.GetType() || !Equals(incident.Location, cause.TargetSite);
        if (changed)
        {
            _failures[module] = new FailureIncident { Stage = stage, OuterType = error.GetType(), CauseType = cause.GetType(), Location = cause.TargetSite, Next = now + 60 };
            DiagnosticLog.Write(_log, module.Name, CoreLogTopics.Lifecycle, $"Disabled after {stage} error.", level: LogLevel.Error, error: error);
            return;
        }
        var current = incident!;
        if (now < current.Next && now >= current.Next - 60) { current.Suppressed++; return; }
        DiagnosticLog.Write(_log, module.Name, CoreLogTopics.Lifecycle, "Module remains disabled after an unresolved error", new (string, object?)[] { ("operation", stage), ("error_type", cause.GetType().FullName), ("error_location", cause.TargetSite), ("repeats_suppressed", current.Suppressed) }, LogLevel.Error);
        current.Next = now + 60; current.Suppressed = 0;
    }
    public void Dispose()
    {
        foreach (var module in _modules) ModuleCleanup.Run(_log, module.Name, module.Shutdown);
        _modules.Clear(); _failures.Clear(); ModuleScheduler.Current.Clear();
    }
}
