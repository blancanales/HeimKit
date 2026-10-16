using HeimKit.Features.AFKRaidWatch;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.AFKNightWatch;
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
            // Production constructors bind all settings before startup, including disabled modules.
            void BindModules() => ConfigurationLoader.Load(root, logger, config => new HeimKit.Shared.IServerModule[] {
                new InfiniteTorches(config, logger, PlayerServicesTests.For(config, logger)), new WeatherGuard(config, logger, PlayerServicesTests.For(config, logger)), new AFKNightWatch(config, logger, PlayerServicesTests.For(config, logger)), new AFKRaidWatch(config, logger, PlayerServicesTests.For(config, logger)) });
            string SavedValue(string section, string key)
            {
                string current = "";
                foreach (string line in File.ReadAllLines(path))
                {
                    string text = line.Trim();
                    if (text.StartsWith("[") && text.EndsWith("]")) current = text[1..^1];
                    else if (current == section && text.StartsWith(key + " = ")) return text[(key.Length + 3)..];
                }
                throw new Exception("Setting missing from saved config: " + section + "." + key);
            }
            BindModules();
            foreach (string section in new[] { "Diagnostics", "Torches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch" })
                Check(SavedValue(section, "DebugMode") == "false", "First-use debug defaults missing");
            File.WriteAllText(path, "[Diagnostics]\nDebugMode = true\n[Torches]\nEnabled = false\n[WeatherGuard]\nEnabled = false\n[AFKNightWatch]\nEnabled = false\n[AFKRaidWatch]\nEnabled = false\n");
            BindModules();
            foreach (string section in new[] { "Torches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch" })
            {
                Check(SavedValue(section, "DebugMode") == "false", "Missing debug default not saved for disabled feature");
                Check(SavedValue(section, "Enabled") == "false", "Default addition changed disabled feature");
            }
            Check(SavedValue("Diagnostics", "DebugMode") == "true", "Existing global debug value changed");
            File.WriteAllText(path, "[Torches]\nDebugMode = true\n[WeatherGuard]\nDebugMode = false\n[AFKNightWatch]\nEnabled = false\n[AFKRaidWatch]\nEnabled = false\n");
            BindModules();
            Check(SavedValue("Torches", "DebugMode") == "true" && SavedValue("WeatherGuard", "DebugMode") == "false" &&
                SavedValue("AFKNightWatch", "DebugMode") == "false" && SavedValue("Diagnostics", "DebugMode") == "false", "Mixed existing debug values changed or missing defaults omitted");
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
                    for (int i = 0; i < 20; i++) DebugLoggingResolver.Resolve(settings.Config, "Torches", settings.Torch.GlobalDebugMode, logger, settings.Torch.DebugMode);
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
            // Exercise each real Settings class: feature-only selection and hidden precedence.
            foreach (string selected in new[] { "Torches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch" })
            foreach (bool global in new[] { false, true })
            foreach (bool local in new[] { false, true })
            foreach (string? hidden in new string?[] { null, "false", "true", "invalid", "" })
            {
                File.WriteAllText(path, "[Diagnostics]\nDebugMode = " + global + "\n[" + selected + "]\nDebugMode = " + local + "\n" +
                    (hidden == null ? "" : "DisableDebugLogging = " + hidden + "\n"));
                var policies = ConfigurationLoader.Load(root, logger, file => new[] {
                    new InfiniteTorchesSettings(file, logger).DebugLogging,
                    new WeatherGuardSettings(file, logger).DebugLogging,
                    new AFKNightWatchSettings(file, logger).DebugLogging,
                    new AFKRaidWatchSettings(file, logger).DebugLogging });
                int index = Array.IndexOf(new[] { "Torches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch" }, selected);
                for (int i = 0; i < policies.Length; i++)
                    Check(policies[i].Enabled == (i == index ? (global || local) && (hidden == null || hidden == "false") : global), "Per-feature selection/hidden precedence leaked between features");
                var reload = new ConfigFile(path, false) { SaveOnConfigSet = false };
                var savedPolicies = new[] { new InfiniteTorchesSettings(reload, logger).DebugLogging, new WeatherGuardSettings(reload, logger).DebugLogging, new AFKNightWatchSettings(reload, logger).DebugLogging, new AFKRaidWatchSettings(reload, logger).DebugLogging };
                Check(savedPolicies.Select(p => p.Enabled).SequenceEqual(policies.Select(p => p.Enabled)), "Feature diagnostic selections changed after save/reload");
                string saved = File.ReadAllText(path);
                Check(saved.Split("DisableDebugLogging").Length - 1 == (hidden == null ? 0 : 1), "New feature switches generated hidden overrides");
                if (selected == "Torches" && !global && local && hidden == null)
                {
                    var diagnostic = new InfiniteTorchesDiagnostics(reload, logger);
                    Check(diagnostic.IsDebugEnabled, "Config-based torch diagnostics ignored feature-only switch");
                    var entry = reload.Bind("Torches", "DebugMode", false); entry.Value = false;
                    Check(!diagnostic.IsDebugEnabled, "Policy did not read feature setting");
                }
            }
            // Suppression leaves ordinary lifecycle and version warnings visible.
            File.WriteAllText(path, "[Diagnostics]\nDebugMode = true\n[Torches]\nEnabled = true\nDisableDebugLogging = true\n[WeatherGuard]\nEnabled = true\nDisableDebugLogging = true\n");
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var torches = new InfiniteTorches(config, logger, PlayerServicesTests.For(config, logger));
            var weather = new WeatherGuard(config, logger, PlayerServicesTests.For(config, logger));
            messages.Clear(); torches.Initialize("1.0.20"); weather.Initialize("1.0.20");
            Check(torches.IsActive && weather.IsActive && messages.Count(m => m.Text.EndsWith(": Enabled.")) == 2 &&
                messages.Where(m => m.Level == LogLevel.Debug).Count() == 2 && messages.Where(m => m.Level == LogLevel.Debug).All(m => m.Text.Contains("Debug logging suppressed by DisableDebugLogging")), "Suppression changed gameplay enablement or leaked recurring diagnostics.");
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
        System.Console.WriteLine("PASS: global/feature/hidden debug matrix across all four features, selective diagnostics and save/reload preservation; suppression-only debug matrix, module independence, hidden omission/manual preservation, invalid fallback/bounded warnings, migration, lazy diagnostics and retained lifecycle warnings.");
    }
}
