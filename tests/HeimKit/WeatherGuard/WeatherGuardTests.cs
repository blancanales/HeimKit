using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;

internal static class WeatherGuardTests
{
    private static void Check(bool valid, string message)
    { if (!valid) throw new Exception("WeatherGuard check: " + message); }
    public static void Run()
    {
        foreach (bool torchesOn in new[] { false, true })
        foreach (bool weatherOn in new[] { false, true })
        foreach (string version in new[] { "1.0.17", "1.0.18" })
        {
            var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            config.Bind("Torches", "Enabled", torchesOn);
            config.Bind("WeatherGuard", "Enabled", weatherOn);
            using var log = new ManualLogSource("WeatherGuard lifecycle checks");
            var messages = new List<string>(); log.LogEvent += (_, args) => messages.Add(args.Data.ToString()!);
            var torches = new InfiniteTorches(config, log);
            var weather = new WeatherGuard(config, log);
            torches.Initialize(version); weather.Initialize(version);
            Check(torches.IsActive == (torchesOn && version == "1.0.17"), "torch isolation");
            Check(weather.IsActive == (weatherOn && version == "1.0.17"), "weather isolation/version guard");
            Check(weather.Name == "WeatherGuard" && messages[1].StartsWith("WeatherGuard: "), "exact feature name");
            if (weather.IsActive) Check(messages[2].Contains("player-built") && messages[2].Contains("No logout") &&
                messages[2].Contains("creators are preserved") && messages[2].Contains("World-created claims are inactive"), "honest startup scope and validation");
            weather.Shutdown(); weather.Shutdown(); torches.Shutdown();
            Check(!weather.IsActive, "repeatable shutdown");
        }
        var folder = Path.Combine(AppContext.BaseDirectory, "weather-config-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        using var configLog = new ManualLogSource("WeatherGuard config checks");
        try
        {
            var path = Path.Combine(folder, "heimkit.cfg");
            ConfigurationLoader.Load(folder, configLog, config => new WeatherGuard(config, configLog));
            var first = File.ReadAllText(path);
            Check(first.Contains("[WeatherGuard]") && first.Contains("Enabled = false"), "complete first-use config with one feature switch");
            foreach (bool enabled in new[] { false, true })
            {
                File.WriteAllText(path, "[WeatherGuard]\nEnabled = " + enabled + "\n[Torches]\nEnabled = false\n[Diagnostics]\nDebugMode = true\n");
                ConfigurationLoader.Load(folder, configLog, config => { _ = new InfiniteTorches(config, configLog); return new WeatherGuard(config, configLog); });
                var loaded = new ConfigFile(path, false) { SaveOnConfigSet = false };
                Check(loaded.Bind("WeatherGuard", "Enabled", !enabled).Value == enabled &&
                    loaded.Bind("Diagnostics", "DebugMode", false).Value && !loaded.Bind("Torches", "Enabled", true).Value,
                    "direct binding preserves existing WeatherGuard, torches and debug settings");
            }
        }
        finally { Directory.Delete(folder, true); }
        System.Console.WriteLine("PASS: independent WeatherGuard/torch lifecycle, compatibility, exact naming, auto-created single-switch config, existing-setting preservation. No Unity world is run.");
        WeatherGuardRulesTests.Run();
    }
}
