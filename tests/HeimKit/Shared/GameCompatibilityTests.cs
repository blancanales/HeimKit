using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Shared;

internal static class GameCompatibilityTests
{
    public static void Run()
    {
        foreach (bool enabled in new[] { false, true })
        foreach (string version in new[] { "1.0.20", "1.0.17", "1.0.18", "1.0.19", "1.0.21", "" })
        {
            var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            foreach (string section in new[] { "Torches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch" })
                config.Bind(section, "Enabled", enabled);
            using var log = new ManualLogSource("Game compatibility tests");
            var warnings = new List<string>();
            log.LogEvent += (_, args) => { if (args.Level == LogLevel.Warning) warnings.Add(args.Data.ToString()!); };
            var players = PlayerServicesTests.For(config, log);
            IServerModule[] modules = { new InfiniteTorches(config, log, players), new WeatherGuard(config, log, players),
                new AFKNightWatch(config, log, players), new AFKRaidWatch(config, log, players) };
            try
            {
                foreach (var module in modules)
                {
                    module.Initialize(version);
                    if (module.IsActive != (enabled && version == "1.0.20"))
                        throw new Exception(module.Name + " accepted an unsupported version or ignored Enabled: " + version);
                    bool warned = warnings.Any(message => message.Contains(module.Name + ":") && message.Contains("unsupported"));
                    if (warned != (enabled && version != "1.0.20"))
                        throw new Exception(module.Name + " compatibility warning does not match its startup state.");
                }
            }
            finally { foreach (var module in modules) module.Shutdown(); }
        }
        System.Console.WriteLine("PASS: all four modules accept the supported game version, reject older/newer/empty versions and respect independent enable switches.");
    }
}
