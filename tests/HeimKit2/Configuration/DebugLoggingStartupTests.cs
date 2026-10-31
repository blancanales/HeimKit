using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Core.Players;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class DebugLoggingStartupTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        DiagnosticLog.StartSession();
        using var logger = new ManualLogSource("Debug startup checks");
        var records = new List<(LogLevel Level, string Text)>();
        logger.LogEvent += (_, args) => records.Add((args.Level, LogCapture.Text(args.Data)));
        string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg");
        string[] names = { "InfiniteTorches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch" };
        string[] sections = { "Torches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch" };
        try
        {
            foreach (int selected in Enumerable.Range(0, 4))
            foreach (bool global in new[] { false, true })
            foreach (bool local in new[] { false, true })
            foreach (bool suppressed in new[] { false, true })
            {
                File.WriteAllText(path, $"[Diagnostics]\nDebugMode = {global}\n[{sections[selected]}]\nDebugMode = {local}\nDisableDebugLogging = {suppressed}\n");
                var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
                using var players = new PlayerServices(config, logger);
                IServerModule[] modules = {
                    new InfiniteTorches(config, logger, players), new WeatherGuard(config, logger, players),
                    new AFKNightWatch(config, logger, players), new AFKRaidWatch(config, logger, players)
                };
                DiagnosticLog.StartSession(); records.Clear();
                foreach (var module in modules) module.Initialize(HeimKit.Configuration.GameVersionCompatibility.AuditedVersion);
                var declarations = records.Where(r => r.Text.Contains("Debug logging ")).ToArray();
                int expected = global ? 4 : local || suppressed ? 1 : 0;
                Check(declarations.Length == expected && declarations.All(r => r.Level == LogLevel.Debug), "Per-feature startup policy count/severity incorrect");
                if (global || local || suppressed)
                {
                    var line = declarations.Single(r => r.Text.StartsWith(names[selected] + ".")).Text;
                    string source = suppressed ? "hidden_override" : global && local ? "global_and_feature" : local ? "feature" : "global";
                    Check(line.Contains("log_schema=2") && line.Contains("source=" + source) && line.Contains("configured_debug_enabled=" + ((global || local) && !suppressed).ToString().ToLowerInvariant()),
                        "Startup policy did not explain source and effective suppression");
                }
                foreach (var module in modules) module.Shutdown();
            }
            // WeatherGuard feature-only settings feed the same captured policy used by its controller.
            File.WriteAllText(path, "[Diagnostics]\nDebugMode = false\n[WeatherGuard]\nDebugMode = true\n");
            var weatherConfig = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var weather = new WeatherGuardSettings(weatherConfig, logger);
            Check(weather.DebugLogLevel.Value == DebugLogLevel.Detailed && weather.DebugLogging.ConfiguredEnabled, "Feature-only default Detailed diagnostics not enabled");
            DiagnosticLog.StartSession(); records.Clear();
            var diagnostics = new WeatherGuardDiagnostics(logger, weather.DebugLogging, weather.DebugLogLevel.Value, false);
            diagnostics.Report(0, () => new WeatherGuardStatus());
            Check(records.Any(r => r.Level == LogLevel.Debug && r.Text.Contains(".scope:")) && records.Any(r => r.Text.Contains("protection and network activity")),
                "Detailed feature-only diagnostics did not emit without global debug");
            // Invoke the actual loader listener against a memory writer: prove filtering independently
            // of native Unity/gameplay and without editing a server's loader configuration.
            using var writer = new StringWriter();
            using var disk = (DiskLogListener)RuntimeHelpers.GetUninitializedObject(typeof(DiskLogListener));
            typeof(DiskLogListener).GetProperty("LogWriter")!.SetValue(disk, writer);
            disk.DisplayedLogLevel = LogLevel.Info | LogLevel.Warning | LogLevel.Error;
            var debug = new LogEventArgs("WeatherGuard Debug probe", LogLevel.Debug, logger);
            disk.LogEvent(logger, debug);
            Check(writer.ToString().Length == 0, "Loader unexpectedly displayed filtered Debug");
            disk.DisplayedLogLevel |= LogLevel.Debug;
            disk.LogEvent(logger, debug);
            Check(writer.ToString().Contains("WeatherGuard Debug probe"), "Loader did not display Debug after enabling its filter");
            // Exercise the production loader/save/layout and all four real module settings,
            // then deliver WeatherGuard records through the actual disk listener. Use explicit
            // Medium as well as Detailed so this does not rely on the later default change.
            string directory = Path.Combine(AppContext.BaseDirectory, "feature-only-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                foreach (var level in new[] { DebugLogLevel.Medium, DebugLogLevel.Detailed })
                foreach (bool diskDebug in new[] { false, true })
                {
                    DiagnosticLog.StartSession(); records.Clear(); writer.GetStringBuilder().Clear();
                    disk.DisplayedLogLevel = LogLevel.Info | (diskDebug ? LogLevel.Debug : LogLevel.None);
                    File.WriteAllText(Path.Combine(directory, "heimkit.cfg"),
                        "[Diagnostics]\nDebugMode = false\n[WeatherGuard]\nDebugMode = true\nDisableDebugLogging = false\nDebugLogLevel = " + level + "\n");
                    logger.LogEvent += disk.LogEvent;
                    PlayerServices? players = null;
                    IServerModule[] modules = Array.Empty<IServerModule>();
                    try
                    {
                        var selected = ConfigurationLoader.Load(directory, logger, file => {
                            players = new PlayerServices(file, logger);
                            var settings = new WeatherGuardSettings(file, logger);
                            modules = new IServerModule[] {
                                new InfiniteTorches(file, logger, players), new WeatherGuard(file, logger, players),
                                new AFKNightWatch(file, logger, players), new AFKRaidWatch(file, logger, players)
                            };
                            return settings;
                        });
                        foreach (var module in modules) module.Initialize(HeimKit.Configuration.GameVersionCompatibility.AuditedVersion);
                        new WeatherGuardDiagnostics(logger, selected.DebugLogging, selected.DebugLogLevel.Value, false)
                            .Report(0, () => new WeatherGuardStatus());
                        Check(!selected.GlobalDebugMode.Value && selected.DebugMode.Value && selected.DebugLogging.ConfiguredEnabled,
                            "Production loader/save changed feature-only debug selection");
                        Check(records.Any(r => r.Level == LogLevel.Debug && r.Text.Contains("Waiting for joined players and dedicated-server world")) &&
                            records.Any(r => r.Level == LogLevel.Debug && r.Text.StartsWith("WeatherGuard.scope:")),
                            "Feature-only startup/summary emission failed after production loading");
                        Check(!records.Any(r => r.Level == LogLevel.Debug && names.Where(n => n != "WeatherGuard").Any(n => r.Text.StartsWith(n + "."))),
                            "Feature-only setting enabled another module's Debug");
                        Check(writer.ToString().Contains("WeatherGuard.scope:") == diskDebug,
                            "Production feature-only records did not follow the loader's Debug filter");
                    }
                    finally
                    {
                        foreach (var module in modules) module.Shutdown();
                        players?.Dispose(); logger.LogEvent -= disk.LogEvent;
                    }
                }
            }
            finally
            {
                string resolved = Path.GetFullPath(directory);
                string allowed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
                Directory.Delete(resolved, true);
            }
            // Default changes preserve owners' explicit Medium selection.
            weatherConfig.Save();
            weatherConfig.Bind("WeatherGuard", "DebugLogLevel", DebugLogLevel.Detailed).Value = DebugLogLevel.Medium;
            weatherConfig.Save();
            var reloaded = new WeatherGuardSettings(new ConfigFile(path, false) { SaveOnConfigSet = false }, logger);
            Check(reloaded.DebugLogLevel.Value == DebugLogLevel.Medium, "Detailed default overwrote an existing Medium selection");
        }
        finally { File.Delete(path); DiagnosticLog.StartSession(); }
        System.Console.WriteLine("PASS: per-feature Debug startup source/override matrix, WeatherGuard feature-only Detailed defaults/emission, production loader/save/layout with feature-only Medium and Detailed, actual BepInEx listener filtering and preserved Medium configuration.");
    }
}
