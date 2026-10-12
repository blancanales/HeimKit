using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.WeatherGuard.Models;

internal static class WeatherGuardControlsTests
{
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    public static void Run()
    {
        // Wet-source truth table includes dry/roofed, exposed rain, covered underwater and overlapping rain/underwater.
        foreach (bool native in new[] { false, true })
        foreach (bool rainProtection in new[] { false, true })
        foreach (bool waterProtection in new[] { false, true })
        foreach (var exposure in new[] { (Rain: false, Underwater: false), (Rain: true, Underwater: false),
            (Rain: false, Underwater: true), (Rain: true, Underwater: true) })
        {
            bool expected = native && (rainProtection && waterProtection ||
                (exposure.Rain || exposure.Underwater) && !(exposure.Rain && !rainProtection) && !(exposure.Underwater && !waterProtection));
            Check(WeatherGuardRules.SuppressWeatherDamage(native, rainProtection, waterProtection, exposure.Rain, exposure.Underwater) == expected,
                "Weather source selection changed native or overlapping-source semantics.");
        }
        var exclusions = WeatherGuardRules.ParseExcludedPrefabs(" wood_floor,wood_floor,woodwall,Wood_Floor,*,wood*,invalid name,,foo/bar ", out int rejected);
        Check(exclusions.Count == 3 && rejected == 5 && exclusions.Contains("wood_floor") && exclusions.Contains("Wood_Floor") && !exclusions.Contains("wood_floor_1x1"),
            "Exact exclusion parsing accepted patterns, lost case or rejected valid trimmed identifiers.");
        Check(WeatherGuardRules.ParseExcludedPrefabs("", out int none).Count == 0 && none == 0, "Empty exclusion default not neutral.");
        var oversized = string.Join(",", Enumerable.Range(0, 1025).Select(i => "piece_" + i));
        Check(WeatherGuardRules.ParseExcludedPrefabs(oversized, out rejected).Count == 1024 && rejected == 1, "Exclusion memory not bounded.");
        Check(WeatherGuardRules.SelectionReason("container", "wood_floor", exclusions, false, false) == "container", "Config changed baseline container exclusion.");
        Check(WeatherGuardRules.SelectionReason("world-created", "wood_floor", exclusions, true, true) == "excluded_prefab", "Exact exclusion priority missing.");
        Check(WeatherGuardRules.SelectionReason("eligible", "wood_floor", exclusions, false, false) == "excluded_prefab", "Disabled options obscured explicit exclusion.");
        Check(WeatherGuardRules.SelectionReason("eligible", "wood_floor_1x1", exclusions, false, false) == "weather_protection_disabled", "Both-off selection still acquired pieces.");
        Check(WeatherGuardRules.SelectionReason("world-created", "other", exclusions, true, false) == "world-created", "Config broadened creator eligibility.");
        Check(WeatherGuardRules.SelectionReason("eligible", "WOOD_FLOOR", exclusions, true, true) == "eligible", "Exclusion matching is not ordinal exact.");

        var coverage = new CoverageSnapshot { Observed = true, State = "ready", RainEnabled = true, UnderwaterEnabled = true };
        coverage.Add("eligible", true, true, "none");
        coverage.Add("eligible", true, false, "waiting_terrain");
        coverage.Add("eligible", false, true, "waiting_terrain");
        coverage.Add("excluded_prefab", true, true, "none");
        coverage.Add("world-created", false, true, "none");
        Check(coverage.Eligible == 3 && coverage.Protected == 1 && coverage.Waiting == 2 && coverage.Skipped == 2 && coverage.UnloadedEligible == 1 &&
            coverage.SkipReasons["excluded_prefab"] == 1 && coverage.WaitReasons["waiting_instance"] == 1, "Coverage claimed unloaded/excluded objects as protected or miscounted reasons.");
        using var logger = new ManualLogSource("WeatherGuard controls checks");
        var messages = new List<(string Text, LogLevel Level)>();
        logger.LogEvent += (_, args) => messages.Add((args.Data.ToString()!, args.Level));
        var suppressed = new WeatherGuardDiagnostics(logger, false);
        suppressed.Coverage(coverage);
        Check(messages.Count == 1 && messages[0].Level == LogLevel.Info && messages[0].Text.Contains("Requested coverage report") && messages[0].Text.Contains("protected 1") &&
            messages[0].Text.Contains("rest of the world is unobserved"), "Requested coverage hidden by debug-off or overclaimed global scope.");
        suppressed.Coverage(new CoverageSnapshot { State = "waiting_for_world" });
        Check(messages[^1].Text.Contains("counts are unknown"), "Unobserved scope falsely reported measured zeros.");

        var medium = new WeatherGuardDiagnostics(logger, true, DebugLogLevel.Medium, false);
        medium.Report(0, () => throw new Exception(), null, () => new[] { new ProtectionObservation
        { Identity = new() { ["object"] = "partial" }, Protected = true, RainProtection = false, UnderwaterProtection = true, Reason = "eligible" } });
        Check(messages[^1].Level == LogLevel.Debug && messages[^2].Level == LogLevel.Debug && messages[^1].Text.Contains("protected=true") && messages[^1].Text.Contains("rain_protected=false") && messages[^1].Text.Contains("underwater_protected=true"),
            "Medium output presented partial protection as both-channel protection.");
        var root = Path.Combine(AppContext.BaseDirectory, "weather-controls-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "heimkit.cfg");
        var requestPath = Path.Combine(root, WeatherGuardCoverageRequests.FileName);
        try
        {
            var config = new ConfigFile(configPath, false) { SaveOnConfigSet = false };
            var settings = new WeatherGuardSettings(config, logger);
            Check(settings.ProtectRain.Value && settings.ProtectUnderwater.Value && settings.ExcludedPrefabs.Value == "", "Protection defaults changed existing behavior.");
            File.WriteAllText(configPath, "[WeatherGuard]\nDebugLogLevel = 123\n");
            var invalidLevel = new WeatherGuardSettings(new ConfigFile(configPath, false) { SaveOnConfigSet = false }, logger);
            Check(invalidLevel.DebugLogLevel.Value == DebugLogLevel.Medium, "Undefined numeric diagnostic level must fall back to Medium.");
            foreach (var level in new[] { DebugLogLevel.Medium, DebugLogLevel.Detailed })
            {
                File.WriteAllText(configPath, "[WeatherGuard]\nDebugLogLevel = " + level + "\n");
                var validLevel = ConfigurationLoader.Load(root, logger, cfg => new WeatherGuardSettings(cfg, logger));
                Check(validLevel.DebugLogLevel.Value == level, "Diagnostic-level validation reset a recognized setting.");
            }
            File.WriteAllText(configPath, "[WeatherGuard]\nProtectRain = false\nProtectUnderwater = true\nExcludedPrefabs = wood_floor\nDisableDebugLogging = true\n[Diagnostics]\nDebugMode = true\n");
            var migrated = ConfigurationLoader.Load(root, logger, cfg => new WeatherGuardSettings(cfg, logger));
            Check(!migrated.ProtectRain.Value && migrated.ProtectUnderwater.Value && migrated.ExcludedPrefabs.Value == "wood_floor" && !migrated.DebugLogging.Enabled,
                "Existing source/exclusion/hidden settings not preserved.");
            var requests = new WeatherGuardCoverageRequests(root, logger);
            int reports = 0;
            requests.Poll(0, () => reports++);
            Check(reports == 0, "Coverage was produced without a deliberate request.");
            File.WriteAllText(requestPath, "");
            requests.Poll(0.5f, () => reports++);
            Check(reports == 0 && File.Exists(requestPath), "Request polling exceeded its bound.");
            requests.Poll(1, () => { reports++; suppressed.Coverage(coverage); });
            requests.Poll(2, () => reports++);
            Check(reports == 1 && !File.Exists(requestPath), "Coverage request repeated or not consumed.");
            File.WriteAllText(requestPath, "");
            using (var locked = new FileStream(requestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                int warnings = messages.Count(m => m.Level == LogLevel.Warning);
                requests.Poll(3, () => throw new Exception("Locked request executed."));
                requests.Poll(4, () => throw new Exception("Locked request executed."));
                Check(messages.Count(m => m.Level == LogLevel.Warning) == warnings + 1, "Request-access warnings flooded.");
            }
            requests.Poll(5, () => reports++);
            Check(reports == 2, "Unlocked request failed to recover.");
            File.WriteAllText(requestPath, "");
            requests.Poll(6, () => throw new Exception("Report failure"));
            Check(messages[^1].Level == LogLevel.Error && !File.Exists(requestPath), "Report exception escaped or repeated.");
            File.WriteAllText(requestPath, "");
            requests.Poll(7, () => reports++);
            Check(reports == 3, "Report failure broke later requests.");
            // Disabled module still services explicit reports without initializing Unity gameplay.
            var moduleConfig = new ConfigFile(configPath, false) { SaveOnConfigSet = false };
            var module = new WeatherGuard(moduleConfig, logger, PlayerServicesTests.For(moduleConfig, logger));
            module.Initialize("1.0.17");
            File.WriteAllText(requestPath, ""); module.Tick(10);
            Check(!module.IsActive && messages[^1].Text.Contains("module_inactive") && !File.Exists(requestPath), "Inactive module ignored explicit coverage request.");
            module.Shutdown();
            // Gameplay stays gated while operator requests survive every presence state.
            var hub = PlayerServicesTests.For(moduleConfig, logger);
            moduleConfig.Bind("WeatherGuard", "Enabled", false).Value = true;
            module.Initialize("1.0.17");
            var controller = typeof(WeatherGuard).GetField("_controller", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Check(controller.GetValue(module) == null, "Unknown presence initialized WeatherGuard gameplay.");
            Check(module.IsActive && hub.Events.Subscribers == 1, "WeatherGuard did not subscribe while enabled.");
            File.WriteAllText(requestPath, ""); module.Tick(11);
            Check(messages[^1].Text.Contains("player_presence_unavailable"), "Unknown presence report was misleading.");
            hub.Events.Observe(false);
            File.WriteAllText(requestPath, ""); module.Tick(12);
            Check(module.IsActive && messages[^1].Text.Contains("server_empty"), "Empty server disabled the module or blocked its report.");
            hub.Events.Observe(true); hub.Events.Observe(true);
            File.WriteAllText(requestPath, ""); module.Tick(13);
            Check(controller.GetValue(module) == null && module.IsActive && hub.Activity.Consumers == 0 && messages[^1].Text.Contains("waiting_for_world"), "WeatherGuard bypassed world readiness or acquired AFK tracking.");
            hub.Events.Observe(null);
            File.WriteAllText(requestPath, ""); module.Tick(14);
            Check(messages[^1].Text.Contains("player_presence_unavailable"), "Lost presence retained occupied report state.");
            typeof(WeatherGuard).GetMethod("Fail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(module, new object[] { new InvalidOperationException("Simulated feature failure") });
            Check(!module.IsActive && hub.Events.Subscribers == 0, "WeatherGuard failure retained its registration.");
            hub.Events.Observe(false); hub.Events.Observe(true);
            Check(!module.IsActive, "Failed WeatherGuard restarted on connection.");
            module.Initialize("1.0.17"); module.Shutdown(); module.Shutdown();
            Check(hub.Events.Subscribers == 0, "Repeated WeatherGuard shutdown retained its registration.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var allowed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path.");
            Directory.Delete(resolved, true);
        }
        System.Console.WriteLine("PASS: rain/underwater switches and overlap, exact bounded exclusions/priority, honest debug-off coverage, one-shot local admin requests, access/error recovery, presence-gated gameplay, retained paused reports, isolated lifecycle cleanup and config preservation.");
    }
}
