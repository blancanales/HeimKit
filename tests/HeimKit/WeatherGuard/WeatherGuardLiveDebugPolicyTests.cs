using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;

internal static class WeatherGuardLiveDebugPolicyTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        DiagnosticLog.StartSession();
        using var log = new ManualLogSource("WeatherGuard live debug policy");
        var messages = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => messages.Add((args.Level, LogCapture.Text(args.Data)));
        foreach (var level in new[] { DebugLogLevel.Medium, DebugLogLevel.Detailed })
        {
            var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            var settings = new WeatherGuardSettings(config, log);
            settings.GlobalDebugMode.Value = false; settings.DebugMode.Value = false;
            var diagnostics = new WeatherGuardDiagnostics(log, settings.DebugLogging, level, false);
            messages.Clear(); diagnostics.Report(0, () => throw new Exception("Disabled debug collected evidence"));
            Check(messages.Count == 0, "Disabled policy emitted logs");
            settings.DebugMode.Value = true;
            diagnostics.Report(1, () => new WeatherGuardStatus());
            Check(messages.Any(m => m.Level == LogLevel.Debug && m.Text.Contains(".scope:")), "Feature-only enablement retained the old false snapshot");
            settings.DebugMode.Value = false; messages.Clear();
            diagnostics.Report(62, () => throw new Exception("Disabled feature collected evidence"));
            Check(messages.Count == 0, "Feature disablement retained the old true snapshot");
            settings.GlobalDebugMode.Value = true;
            diagnostics.Report(63, () => new WeatherGuardStatus());
            Check(messages.Any(m => m.Level == LogLevel.Debug), "Live policy lost global inheritance");
            settings.GlobalDebugMode.Value = false; settings.DebugMode.Value = true;
            DiagnosticLog.ServerActivity.Observe(300, false); messages.Clear();
            diagnostics.Report(124, () => throw new Exception("Empty grace pause collected evidence"));
            Check(messages.Count == 0, "Feature-only policy bypassed empty grace");
            DiagnosticLog.ServerActivity.Observe(301, true);
            diagnostics.Report(125, () => new WeatherGuardStatus());
            Check(messages.Any(m => m.Level == LogLevel.Debug), "Feature-only policy failed to recover after a connection");
            DiagnosticLog.StartSession();
        }
        System.Console.WriteLine("PASS: WeatherGuard retains live policy for feature-only enable/disable, global inheritance and empty-grace recovery in Medium and Detailed.");
    }
}
