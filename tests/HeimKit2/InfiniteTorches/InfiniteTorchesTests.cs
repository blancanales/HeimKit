using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Configuration;

internal static class InfiniteTorchesTests
{
    public static void Run()
    {
        // Exercise actual feature configuration and startup logs without a running Unity world.
        var cases = new[]
        {
            (Enabled: true, Version: HeimKit.Configuration.GameVersionCompatibility.AuditedVersion, Active: true, Message: "InfiniteTorches: Enabled."),
            (Enabled: false, Version: HeimKit.Configuration.GameVersionCompatibility.AuditedVersion, Active: false, Message: "InfiniteTorches: Disabled."),
            (Enabled: true, Version: "1.0.18", Active: false, Message: "DISABLED (unsupported Valheim version 1.0.18"),
            (Enabled: false, Version: "1.0.18", Active: false, Message: "InfiniteTorches: Disabled.")
        };
        foreach (var test in cases)
        {
            var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false)
            {
                SaveOnConfigSet = false
            };
            config.Bind("Torches", "Enabled", test.Enabled);
            using var logger = new ManualLogSource("HeimKit checks");
            var messages = new List<string>();
            logger.LogEvent += (_, args) => messages.Add(LogCapture.Text(args.Data));
            var feature = new InfiniteTorches(config, logger, PlayerServicesTests.For(config, logger));
            feature.Initialize(test.Version);
            if (feature.IsActive != test.Active || messages.Count != (test.Enabled && !test.Active ? 2 : 1) || !messages.Any(m => m.Contains(test.Message)))
                throw new Exception($"Incorrect startup state/log: {test}. Logs: {string.Join("; ", messages)}");
            if (!test.Enabled && messages.Any(m => m.Contains(": Enabled.")))
                throw new Exception("Disabled feature falsely logged enabled.");
            feature.Shutdown();
            feature.Shutdown();
            if (feature.IsActive) throw new Exception("Shutdown did not stop the feature.");
            System.Console.WriteLine($"PASS: Enabled={test.Enabled}, version={test.Version}; startup log and repeated shutdown.");
        }
        System.Console.WriteLine("All 4 lifecycle scenarios passed. Live networking is not tested by these checks.");

        var suppliedConfig = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "heimkit.cfg"), false)
        {
            SaveOnConfigSet = false
        };
        using var configLogger = new ManualLogSource("Supplied config check");
        var suppliedFeature = new InfiniteTorches(suppliedConfig, configLogger, PlayerServicesTests.For(suppliedConfig, configLogger));
        if (!suppliedConfig.TryGetEntry<bool>("Diagnostics", "DebugMode", out var diagnostics) || diagnostics.Value)
            throw new Exception("Supplied configuration must default DebugMode off.");
        suppliedConfig.Bind("Torches", "Enabled", true).Value = false;
        suppliedFeature.Initialize(HeimKit.Configuration.GameVersionCompatibility.AuditedVersion);
        if (suppliedFeature.IsActive) throw new Exception("Existing disabled setting was overridden.");
        System.Console.WriteLine("PASS: supplied heimkit.cfg defaults debug mode off and respects an existing disabled setting.");

    }
}
