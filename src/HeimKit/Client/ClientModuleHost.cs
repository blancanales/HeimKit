using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Core.Control.Models;
using HeimKit.Core.Diagnostics;

namespace HeimKit.Client;

internal sealed class ClientModuleHost : IDisposable
{
    private sealed class Entry
    {
        internal IClientModule Module = null!;
        internal ConfigEntry<bool> Enabled = null!;
        internal bool Running, Failed;
    }

    private readonly ClientSettings _settings;
    private readonly ClientLink _link;
    private readonly ManualLogSource _log;
    private readonly List<Entry> _entries = new();

    internal ClientModuleHost(ClientSettings settings, ClientLink link, ManualLogSource log)
    {
        _settings = settings; _link = link; _log = log;
        link.Changed += Reconcile;
    }

    internal void Register(IClientModule module)
    {
        var entry = new Entry { Module = module, Enabled = _settings.BindModule(module.Name) };
        _entries.Add(entry);
        try { module.Initialize(); }
        catch (Exception error) { Fail(entry, error); }
        entry.Enabled.SettingChanged += SettingChanged;
        Reconcile();
    }

    private void SettingChanged(object sender, EventArgs args) => Reconcile();

    private void Reconcile()
    {
        foreach (var entry in _entries)
        {
            if (entry.Failed) continue;
            SwitchStatus? paired = null;
            if (_link.Snapshot is { } snapshot)
                foreach (var value in snapshot.Switches)
                    if (value.Definition.IsModuleSwitch && value.Definition.Module == entry.Module.Name)
                    { paired = value.Status; break; }
            bool running = ClientPolicy.Decide(_link.State, paired, entry.Enabled.Value, entry.Module.RequiresServer) == ClientDecision.Running;
            if (entry.Running == running) continue;
            try { entry.Module.SetRunning(running); entry.Running = running; }
            catch (Exception error) { Fail(entry, error); }
        }
    }

    internal bool HasModules => _entries.Count != 0;

    internal void Tick(float now)
    {
        foreach (var entry in _entries)
        {
            if (!entry.Running) continue;
            try { entry.Module.Tick(now); }
            catch (Exception error) { Fail(entry, error); }
        }
    }

    private void Fail(Entry entry, Exception error)
    {
        entry.Failed = true; entry.Running = false;
        DiagnosticLog.Write(_log, entry.Module.Name, CoreLogTopics.Control, "Client module stopped after an error.", level: LogLevel.Error, error: error);
        Shutdown(entry);
    }

    private void Shutdown(Entry entry)
    {
        try { entry.Module.Shutdown(); }
        catch (Exception error) { DiagnosticLog.Write(_log, entry.Module.Name, CoreLogTopics.Control, "Client module cleanup failed.", level: LogLevel.Error, error: error); }
    }

    public void Dispose()
    {
        _link.Changed -= Reconcile;
        foreach (var entry in _entries)
        {
            entry.Enabled.SettingChanged -= SettingChanged;
            if (!entry.Failed) Shutdown(entry);
        }
        _entries.Clear();
    }
}
