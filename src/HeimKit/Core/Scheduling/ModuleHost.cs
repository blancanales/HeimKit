using System;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;

namespace HeimKit.Core.Scheduling;

internal sealed class ModuleHost : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly List<IServerModule> _modules = new();
    private readonly Dictionary<IServerModule, FailureIncident> _failures = new();
    private readonly HashSet<IServerModule> _started = new();
    private readonly ModuleScheduler _scheduler;
    private bool _wasEmpty = true;
    private float _lastStartup = float.NegativeInfinity;
    private sealed class FailureIncident
    {
        internal string Stage = "";
        internal Type OuterType = null!, CauseType = null!;
        internal MethodBase? Location;
        internal float Next;
        internal long Suppressed;
    }
    internal ModuleHost(ManualLogSource log, ModuleScheduler? scheduler = null) { _log = log; _scheduler = scheduler ?? ModuleScheduler.Current; }
    internal void Register(Func<IServerModule> create, string name)
    {
        try { _modules.Add(create()); }
        catch (Exception error)
        {
            ModuleFailureProtection.Record(name, error, "configuration construction");
            DiagnosticLog.Write(_log, name, CoreLogTopics.Lifecycle, $"Disabled after configuration construction error.", level: LogLevel.Error, error: error);
        }
    }
    internal void Initialize(string version)
    {
        _failures.Clear();
        foreach (var module in _modules)
            try { if (ModuleFailureProtection.MayStart(module.Name)) module.Initialize(version); }
            catch (Exception error) { Fail(module, error, "startup", 0); }
    }
    internal void Tick(float now, bool? occupied = null, bool beginFrame = true)
    {
        if (beginFrame) _scheduler.BeginFrame(now);
        if (occupied == false) { _started.Clear(); _wasEmpty = true; }
        if (occupied == true && _wasEmpty) { _wasEmpty = false; _lastStartup = now; }
        bool startedThisFrame = false;
        int pending = 0;
        // The first pass keeps protection ahead of convenience work, independent of registration order.
        for (int priority = 0; priority < 2; priority++)
        foreach (var module in _modules)
            // Shutdown gates gameplay; inactive ticks still serve local reports.
            try
            {
                bool immediate = module is IImmediateActivation;
                if (immediate != (priority == 0)) continue;
                if (!ModuleFailureProtection.MayStart(module.Name)) continue;
                if (occupied == true && module.IsActive && !_started.Contains(module))
                {
                    if (!immediate && (startedThisFrame || _scheduler.CombatActive ||
                        !_scheduler.HasBudget && now >= _lastStartup && now - _lastStartup < .25f))
                    { pending++; continue; }
                    _started.Add(module);
                    // A protection activation also reserves this update's startup slot.
                    startedThisFrame = true; _lastStartup = now;
                }
                long started = Stopwatch.GetTimestamp();
                try { module.Tick(now); }
                finally { ServerPerformance.Module(module.Name, started); }
                if (_failures.Count > 0 && module.IsActive) _failures.Remove(module);
            }
            catch (Exception error) { Fail(module, error, "runtime", now); }
        PerformanceStages.ArrivalProgress("Core.startup", now, pending, startedThisFrame);
    }
    private void Fail(IServerModule module, Exception error, string stage, float now)
    {
        ModuleFailureProtection.Record(module.Name, error, stage);
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
        _modules.Clear(); _failures.Clear(); _started.Clear(); _scheduler.Clear();
    }
}
