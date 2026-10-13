using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
using System.Runtime.CompilerServices;

internal static class DebugLoggingDeliveryTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        DiagnosticLog.StartSession();
        using var log = new ManualLogSource("Registered delivery checks");
        using var writer = new StringWriter();
        using var disk = (DiskLogListener)RuntimeHelpers.GetUninitializedObject(typeof(DiskLogListener));
        typeof(DiskLogListener).GetProperty("LogWriter")!.SetValue(disk, writer);
        disk.DisplayedLogLevel = LogLevel.Info | LogLevel.Warning | LogLevel.Error;
        Logger.Sources.Add(log); Logger.Listeners.Add(disk);
        try
        {
            var file = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            var global = file.Bind("Diagnostics", "DebugMode", false);
            var feature = file.Bind("WeatherGuard", "DebugMode", true);
            var policy = new DebugLoggingPolicy(global, false, feature);
            policy.ReportStartup(log, "WeatherGuard", true);
            string output = writer.ToString();
            Check(output.Contains("[Warning") && output.Contains("Logging.Disk.LogLevels") && !output.Contains("[Debug"),
                "Registered logger path silently hid requested feature Debug");
            var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Length == 4 && lines[0].StartsWith("[Warning") && lines.Skip(1).All(line => line.StartsWith("[Info")) &&
                lines[1].Contains("BepInEx separately controls") && lines[2].Contains("[Logging.Disk] LogLevels") &&
                lines[2].Contains("LogLevels = Fatal, Error, Warning, Message, Info, Debug") && lines[3].Contains("restart the server"),
                "Detected filtering did not explain cause and remediation at Info immediately after Warning");
            Check(disk.DisplayedLogLevel == (LogLevel.Info | LogLevel.Warning | LogLevel.Error), "Delivery check modified the loader filter");
            disk.DisplayedLogLevel |= LogLevel.Debug; writer.GetStringBuilder().Clear();
            policy.ReportStartup(log, "WeatherGuard", true);
            output = writer.ToString();
            Check(output.Contains("[Debug") && output.Contains("source=feature") && !output.Contains("[Warning") && !output.Contains("[Info"),
                "Registered feature-only Debug failed despite accepting sink");
            disk.DisplayedLogLevel &= ~LogLevel.Debug; writer.GetStringBuilder().Clear();
            new DebugLoggingPolicy(global, true, feature).ReportStartup(log, "WeatherGuard", true);
            Check(writer.ToString().Length == 0, "Intentional feature suppression was misreported as a delivery failure");
        }
        finally { Logger.Listeners.Remove(disk); Logger.Sources.Remove(log); DiagnosticLog.StartSession(); }
        System.Console.WriteLine("PASS: full registered BepInEx source-to-listener route exposes detected Debug filtering at Warning, preserves filters and respects intentional suppression.");
    }
}
