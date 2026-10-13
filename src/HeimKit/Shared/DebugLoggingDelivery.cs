using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HeimKit.Shared;

// Verify the actual registered output listeners, without changing loader configuration.
public static class DebugLoggingDelivery
{
    public static void Report(ManualLogSource logger, string feature)
    {
        var blocked = new List<string>();
        foreach (var listener in Logger.Listeners)
        {
            if (listener is DiskLogListener disk)
            {
                if ((disk.DisplayedLogLevel & LogLevel.Debug) == 0) blocked.Add("Logging.Disk.LogLevels");
            }
            else if (listener is ConsoleLogListener)
            {
                // Supported loader keeps its live console filter in this private setting.
                var field = typeof(ConsoleLogListener).GetField("ConfigConsoleDisplayedLevel", BindingFlags.Static | BindingFlags.NonPublic);
                if (field?.GetValue(null) is ConfigEntry<LogLevel> entry && (entry.Value & LogLevel.Debug) == 0)
                    blocked.Add("Logging.Console.LogLevels");
            }
        }
        if (blocked.Count == 0) return;
        DiagnosticLog.Write(logger, feature, DiagnosticEvent.Lifecycle,
            "Debug logging is enabled, but an active loader output filters out Debug. Add Debug to " + string.Join(" and ", blocked) + " in BepInEx.cfg and restart.", level: LogLevel.Warning);
        DiagnosticLog.Write(logger, feature, DiagnosticEvent.Lifecycle,
            "HeimKit DebugMode enables diagnostic messages; BepInEx separately controls which log levels reach each output.");
        foreach (string setting in blocked)
        {
            string section = setting.Substring(0, setting.LastIndexOf('.'));
            DiagnosticLog.Write(logger, feature, DiagnosticEvent.Lifecycle,
                "In BepInEx/config/BepInEx.cfg, add Debug to [" + section + "] LogLevels. Example: LogLevels = Fatal, Error, Warning, Message, Info, Debug.");
        }
        DiagnosticLog.Write(logger, feature, DiagnosticEvent.Lifecycle,
            "Preserve the other enabled log levels and restart the server to apply the output filters. Global HeimKit DebugMode can remain off when feature DebugMode is on.");
    }
}
