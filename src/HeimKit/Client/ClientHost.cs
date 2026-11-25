using System;
using BepInEx.Logging;
using HeimKit.Core.Lifecycle;
using UnityEngine;

namespace HeimKit.Client;

internal sealed class ClientHost : IDisposable
{
    private readonly ClientLink _link;
    private readonly LocalWorldRuntime _local;
    private readonly ClientModuleHost _modules;
    private readonly ControlPanel _panel;
    private readonly ClientTameTravelDiagnostics _travel;
    private readonly ClientInteractionDiagnostics _interactions;
    private readonly ManualLogSource _log;

    internal ClientHost(GameObject owner, string configDirectory, string version, ManualLogSource log)
    {
        _log = log;
        var settings = new ClientSettings(configDirectory);
        _local = new LocalWorldRuntime(configDirectory, version, log);
        _link = new ClientLink(version, log, _local);
        _modules = new ClientModuleHost(settings, _link, log);
        _panel = new ControlPanel(owner, settings, _link, version, log);
        _travel = new ClientTameTravelDiagnostics(settings.TraceTameTravel, log);
        _interactions = new ClientInteractionDiagnostics(settings.TraceObjectInteractions, log);
    }

    internal void Tick(float now)
    {
        _local.Tick(now);
        _link.Tick(now);
        if (_modules.HasModules) _modules.Tick(now);
        _travel.Tick(now);
        _panel.Tick(now);
    }

    public void Dispose() =>
        ModuleCleanup.Run(_log, "HeimKit", _travel.Dispose, _interactions.Dispose, _panel.Dispose, _modules.Dispose, _link.Dispose, _local.Dispose);
}
