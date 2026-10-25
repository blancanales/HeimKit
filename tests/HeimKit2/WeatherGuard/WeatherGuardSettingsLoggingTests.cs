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

internal static class WeatherGuardSettingsLoggingTests
{
    public static void Run()
    {
        DiagnosticLog.StartSession();
        using var log = new ManualLogSource("WeatherGuard settings logging");
        var records = new List<(LogLevel Level, string Text)>();
        log.LogEvent += (_, args) => records.Add((args.Level, LogCapture.Text(args.Data)));
        foreach (var scenario in new[] {
            (Global: false, Feature: true, Suppressed: false, Level: DebugLogLevel.Detailed, Expect: true),
            (Global: true, Feature: false, Suppressed: false, Level: DebugLogLevel.Detailed, Expect: true),
            (Global: false, Feature: true, Suppressed: false, Level: DebugLogLevel.Medium, Expect: false),
            (Global: false, Feature: false, Suppressed: false, Level: DebugLogLevel.Detailed, Expect: false),
            (Global: true, Feature: true, Suppressed: true, Level: DebugLogLevel.Detailed, Expect: false)
        })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "settings-log-" + Guid.NewGuid() + ".cfg");
            File.WriteAllText(path, $"[Diagnostics]\nDebugMode = {scenario.Global}\n[WeatherGuard]\nDebugMode = {scenario.Feature}\nDisableDebugLogging = {scenario.Suppressed}\nDebugLogLevel = {scenario.Level}\nProtectRain = false\nProtectUnderwater = true\nExcludedPrefabs = wood_floor\n");
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var hub = PlayerServicesTests.For(config, log);
            var module = new WeatherGuard(config, log, hub);
            try
            {
                records.Clear();
                module.Initialize("1.0.20");
                for (int i = 0; i < 4; i++) module.Tick(i);
                var entries = records.Where(record => record.Text.Contains("Loaded settings and processing limits")).ToArray();
                if (entries.Length != (scenario.Expect ? 1 : 0))
                    throw new Exception("Startup settings are missing, repeated or ignore debug level/policy.");
                if (scenario.Expect && (entries[0].Level != LogLevel.Debug ||
                    !entries[0].Text.Contains("rain_protection_enabled=false") ||
                    !entries[0].Text.Contains("underwater_protection_enabled=true") ||
                    !entries[0].Text.Contains("excluded_prefabs=wood_floor") ||
                    !entries[0].Text.Contains("maximum_protected=8192")))
                    throw new Exception("Detailed startup record lost configured values or fixed processing limits.");
                var settings = new WeatherGuardSettings(config, log);
                var diagnostics = new WeatherGuardDiagnostics(log, settings.DebugLogging, scenario.Level, false);
                records.Clear();
                for (int i = 0; i < 3; i++) diagnostics.Report(i * 60, () => new WeatherGuardStatus { Protected = i, RainProtection = false, UnderwaterProtection = true });
                if (records.Any(record => record.Text.Contains("protection_enabled") || record.Text.Contains("debug_log_level") || record.Text.Contains("roof_probe_limit")))
                    throw new Exception("Recurring diagnostics repeat static startup settings.");
                if (settings.DebugLogging.Enabled && !records.Any(record => record.Text.Contains("managed=2")))
                    throw new Exception("Removing settings also removed observed progress.");
            }
            finally { module.Shutdown(); hub.Dispose(); File.Delete(path); }
        }
        System.Console.WriteLine("PASS: one Detailed startup settings record through real module initialization, feature/global policy, Medium/off/hidden suppression and setting-free recurring progress summaries.");
    }
}
