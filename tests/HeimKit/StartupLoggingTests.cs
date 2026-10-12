using HeimKit.Features.AFKRaidWatch;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Shared;

internal static class StartupLoggingTests
{
    public static void Run()
    {
        var factories = new (string Name, string Section, Func<ConfigFile, ManualLogSource, IServerModule> Create)[]
        {
            ("Infinite Torches", "Torches", (config, log) => new InfiniteTorches(config, log, PlayerServicesTests.For(config, log))),
            ("WeatherGuard", "WeatherGuard", (config, log) => new WeatherGuard(config, log, PlayerServicesTests.For(config, log))),
            ("AFKNightWatch", "AFKNightWatch", (config, log) => new AFKNightWatch(config, log, PlayerServicesTests.For(config, log))),
            ("AFKRaidWatch", "AFKRaidWatch", (config, log) => new AFKRaidWatch(config, log, PlayerServicesTests.For(config, log)))
        };
        foreach (var factory in factories)
        foreach (bool enabled in new[] { false, true })
        foreach (bool debug in new[] { false, true })
        foreach (bool suppressed in new[] { false, true })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "startup-" + Guid.NewGuid() + ".cfg");
            try
            {
                File.WriteAllText(path, $"[Diagnostics]\nDebugMode = {debug}\n[{factory.Section}]\nEnabled = {enabled}\nDisableDebugLogging = {suppressed}\n");
                var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
                using var logger = new ManualLogSource("Startup logging checks");
                var messages = new List<(LogLevel Level, string Text)>();
                logger.LogEvent += (_, args) => messages.Add((args.Level, args.Data.ToString()!));
                var feature = factory.Create(config, logger);
                feature.Initialize("1.0.17");
                if (!enabled) { feature.Tick(0); feature.Tick(60); }
                var info = messages.Where(m => m.Level == LogLevel.Info).ToArray();
                if (info.Length != 1 || info[0].Text != $"{factory.Name}: {(enabled ? "Enabled." : "Disabled.")}")
                    throw new Exception("Configuration status must be concise and emitted once: " + factory.Name);
                bool expectDebug = enabled && debug && !suppressed;
                if (messages.Any(m => m.Level == LogLevel.Debug) != expectDebug ||
                    messages.Any(m => m.Text == factory.Name + ": Ready."))
                    throw new Exception("Waiting must be gated Debug and readiness must require actual initialization: " + factory.Name);
                feature.Shutdown();
                messages.Clear();
                feature.Initialize("unsupported");
                if (feature.IsActive || messages.Count(m => m.Level == LogLevel.Warning) != (enabled ? 1 : 0) ||
                    messages.Any(m => m.Text.Contains("elapsed_s") || m.Text.Contains("Ready.")))
                    throw new Exception("Compatibility issue must be concise and visible without false readiness: " + factory.Name);
                feature.Shutdown();
            }
            finally { File.Delete(path); }
        }
        System.Console.WriteLine("PASS: all feature startup statuses are concise, waiting details obey debug/suppression, readiness is not premature, and compatibility issues remain visible.");
    }
}
