using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Control.Models;
using HeimKit.Core.Diagnostics;

namespace HeimKit.Core.Control.Server;

// All three settings have two values, including Normal/Verbose on the existing switch channel.
internal sealed class ServerDiagnosticsControls
{
    private readonly (SwitchDefinition Definition, ConfigEntryBase Entry)[] _controls;

    internal ServerDiagnosticsControls(ConfigFile config)
    {
        (SwitchDefinition, ConfigEntryBase) Control(string key, string label, string help) =>
            (new SwitchDefinition(ControlProtocol.DiagnosticsGroup, key, label, help, false),
             config[new ConfigDefinition("Diagnostics", key)]);

        _controls = new[]
        {
            Control("DebugMode", "Global debug logging", "Enable host debug logging across features."),
            Control("DebugVerbosity", "Log detail", "Verbose includes detailed evidence for enabled diagnostics."),
            Control("TraceObjectInteractions", "Object interaction tracing", "Sample container requests and replies on the host.")
        };
    }

    internal void AddSnapshot(List<SwitchState> switches)
    {
        foreach (var control in _controls)
        {
            bool enabled = control.Entry is ConfigEntry<DebugVerbosity> detail
                ? detail.Value == DebugVerbosity.Verbose : ((ConfigEntry<bool>)control.Entry).Value;
            switches.Add(new SwitchState(control.Definition, enabled, null,
                enabled ? SwitchStatus.On : SwitchStatus.Off, UnavailableReason.None));
        }
    }

    internal SetResult Set(string? key, bool enabled, string actor, ManualLogSource log)
    {
        foreach (var control in _controls)
        {
            if (control.Definition.Key != key) continue;
            var entry = control.Entry;
            object value = entry is ConfigEntry<DebugVerbosity>
                ? (object)(enabled ? DebugVerbosity.Verbose : DebugVerbosity.Normal) : enabled;
            var previous = entry.BoxedValue;
            if (Equals(value, previous)) return SetResult.Applied;

            var config = entry.ConfigFile;
            bool autoSave = config.SaveOnConfigSet;
            try
            {
                config.SaveOnConfigSet = false;
                entry.BoxedValue = value;
                config.Save();
            }
            catch (Exception error)
            {
                entry.BoxedValue = previous;
                DiagnosticLog.Write(log, "HeimKit", CoreLogTopics.Control,
                    $"{actor} could not save Diagnostics.{key}.", level: LogLevel.Error, error: error);
                return SetResult.Failed;
            }
            finally { config.SaveOnConfigSet = autoSave; }

            try { ConfigurationLayout.Apply(config.ConfigFilePath); }
            catch (Exception error)
            {
                DiagnosticLog.Write(log, "HeimKit", CoreLogTopics.Config,
                    "Could not organize saved configuration sections.", level: LogLevel.Warning, error: error);
            }
            DiagnosticLog.Write(log, "HeimKit", CoreLogTopics.Control, $"{actor} set Diagnostics.{key} to {entry.BoxedValue}.");
            return SetResult.Applied;
        }
        return SetResult.UnknownSwitch;
    }
}
