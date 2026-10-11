using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;

internal static class DebugLoggingResolverTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "debug-policy-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "heimkit.cfg");
        using var logger = new ManualLogSource("Debug suppression checks");
        var messages = new List<(string Text, LogLevel Level)>();
        logger.LogEvent += (_, args) => messages.Add((args.Data.ToString()!, args.Level));
        try
        {
            foreach (bool global in new[] { false, true })
            foreach (string? torchOverride in new string?[] { null, "false", "true", "invalid", "" })
            foreach (string? weatherOverride in new string?[] { null, "false", "true" })
            {
                messages.Clear();
                string Option(string? value) => value == null ? "" : "DisableDebugLogging = " + value + "\n";
                File.WriteAllText(path, "[Diagnostics]\nDebugMode = " + global + "\nLogStatus = false\n[Torches]\n" + Option(torchOverride) +
                    "[WeatherGuard]\n" + Option(weatherOverride));
                var settings = ConfigurationLoader.Load(root, logger, config =>
                    (Torch: new InfiniteTorchesSettings(config, logger), Weather: new WeatherGuardSettings(config, logger), Config: config));
                bool torchEnabled = global && (torchOverride == null || torchOverride == "false");
                bool weatherEnabled = global && (weatherOverride == null || weatherOverride == "false");
                Check(settings.Torch.DebugLogging.Enabled == torchEnabled && settings.Weather.DebugLogging.Enabled == weatherEnabled,
                    "Global/override matrix or feature independence failed.");
                string saved = File.ReadAllText(path);
                Check(!saved.Contains("LogStatus"), "Retired category migration failed.");
                var expectedHidden = (torchOverride != null ? 1 : 0) + (weatherOverride != null ? 1 : 0);
                Check(saved.Split("DisableDebugLogging").Length - 1 == expectedHidden, "Hidden override generated or lost.");
                settings.Config.Save();
                var reloaded = new ConfigFile(path, false) { SaveOnConfigSet = false };
                var reloadTorch = new InfiniteTorchesSettings(reloaded, logger);
                var reloadWeather = new WeatherGuardSettings(reloaded, logger);
                Check(reloadTorch.DebugLogging.Enabled == torchEnabled && reloadWeather.DebugLogging.Enabled == weatherEnabled,
                    "Manual hidden override changed after save/reload.");
                if (torchOverride != null) Check(reloaded.Bind("Torches", "DisableDebugLogging", "sentinel").Value == torchOverride, "Manual torch override text was rewritten.");
                if (weatherOverride != null) Check(reloaded.Bind("WeatherGuard", "DisableDebugLogging", "sentinel").Value == weatherOverride, "Manual weather override text was rewritten.");
                if (torchOverride == "invalid" || torchOverride == "")
                {
                    int warnings = messages.Count(m => m.Level == LogLevel.Warning);
                    Check(warnings == 2, "Invalid override must warn once per loaded configuration, including global-off.");
                    for (int i = 0; i < 20; i++) DebugLoggingResolver.Resolve(settings.Config, "Torches", settings.Torch.DebugMode, logger);
                    Check(messages.Count(m => m.Level == LogLevel.Warning) == warnings, "Invalid override warning flooded.");
                }
                if (!torchEnabled)
                {
                    var diagnostic = new InfiniteTorchesDiagnostics(settings.Torch.DebugLogging, logger);
                    diagnostic.Report(0, () => throw new Exception("Suppressed torch summary was collected."));
                    diagnostic.LogRefillRequest(0, () => throw new Exception("Suppressed torch detail was collected."));
                    Check(!diagnostic.IsDebugEnabled, "Torch diagnostic policy not applied.");
                }
                if (!weatherEnabled)
                {
                    foreach (var level in new[] { DebugLogLevel.Medium, DebugLogLevel.Detailed })
                    {
                        var diagnostic = new WeatherGuardDiagnostics(logger, settings.Weather.DebugLogging.Enabled, level, true);
                        int before = messages.Count;
                        diagnostic.Report(0, () => throw new Exception("Suppressed weather counters collected."),
                            () => throw new Exception("Suppressed rain histories collected."), () => throw new Exception("Suppressed medium status collected."));
                        diagnostic.Sample(0, () => throw new Exception("Suppressed weather samples collected."));
                        Check(!diagnostic.TryWearSample(0, "piece", true) && !diagnostic.ReserveCommand(0) && messages.Count == before,
                            "Suppressed weather evidence reserved or emitted.");
                    }
                }
            }
            // Suppression leaves ordinary lifecycle and version warnings visible.
            File.WriteAllText(path, "[Diagnostics]\nDebugMode = true\n[Torches]\nEnabled = true\nDisableDebugLogging = true\n[WeatherGuard]\nEnabled = true\nDisableDebugLogging = true\n");
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var torches = new InfiniteTorches(config, logger);
            var weather = new WeatherGuard(config, logger);
            messages.Clear(); torches.Initialize("1.0.17"); weather.Initialize("1.0.17");
            Check(torches.IsActive && weather.IsActive && messages.Count(m => m.Text.Contains("ENABLED")) == 2 &&
                messages.Count(m => m.Text.Contains("debug_suppressed=true")) == 2, "Suppression changed gameplay enablement or hid startup.");
            torches.Initialize("unsupported"); weather.Initialize("unsupported");
            Check(messages.Count(m => m.Level == LogLevel.Warning) == 2, "Suppression hid compatibility warnings.");
            torches.Shutdown(); weather.Shutdown();
            // Removing a manual key restores inheritance on the next configuration load.
            File.WriteAllText(path, "[Diagnostics]\nDebugMode = true\n[Torches]\n[WeatherGuard]\n");
            var clean = new ConfigFile(path, false) { SaveOnConfigSet = false };
            Check(new InfiniteTorchesSettings(clean).DebugLogging.Enabled && new WeatherGuardSettings(clean).DebugLogging.Enabled,
                "Removing override did not restore inheritance after restart.");
            Check(!File.ReadAllText(Path.Combine("config", "heimkit.cfg")).Contains("DisableDebugLogging"), "Reference config exposes hidden override.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var allowed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path.");
            Directory.Delete(resolved, true);
        }
        System.Console.WriteLine("PASS: suppression-only debug matrix, module independence, hidden omission/manual preservation, invalid fallback/bounded warnings, migration, lazy diagnostics and retained lifecycle warnings.");
    }
}
