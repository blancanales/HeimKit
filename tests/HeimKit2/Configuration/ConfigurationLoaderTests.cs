using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.SharedMap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Configuration;

internal static class ConfigurationLoaderTests
{
    private static void AssertSharedFirst(string contents)
    {
        int diagnostics = contents.IndexOf("[Diagnostics]", StringComparison.Ordinal);
        int activity = contents.IndexOf("[PlayerActivity]", StringComparison.Ordinal);
        foreach (string feature in new[] { "Torches", "WeatherGuard", "AFKNightWatch", "AFKRaidWatch", "SharedMap", "DoorKeeper", "ForestRenewal", "SeaSalvage", "TameTravel", "HeimMender" })
        {
            int at = contents.IndexOf("[" + feature + "]", StringComparison.Ordinal);
            if (diagnostics < 0 || activity < 0 || at < 0 || diagnostics > at || activity > at)
                throw new Exception("Shared settings must precede every feature section.");
        }
    }
    public static void Run()
    {
        // Use a new isolated directory to exercise real file creation and reload, not just in-memory settings.
        var scratch = Path.Combine(AppContext.BaseDirectory, "config-check-" + Guid.NewGuid());
        var directory = Path.Combine(scratch, "BepInEx", "config");
        var path = Path.Combine(directory, "heimkit.cfg");
        var setupMessages = new List<string>();
        using var setupLogger = new ManualLogSource("First-use config checks");
        setupLogger.LogEvent += (_, args) => setupMessages.Add(LogCapture.Text(args.Data));
        try
        {
            ConfigurationLoader.Load(directory, setupLogger, config => new InfiniteTorches(config, setupLogger, PlayerServicesTests.For(config, setupLogger)));
            if (!File.Exists(path)) throw new Exception("First-use config was not created in the correct folder.");
            var text = File.ReadAllText(path);
            foreach (string setting in new[] { "[Torches]", "Enabled = true", "IntervalSeconds = 5", "MaxMessagesPerPass = 32", "Prefabs = piece_groundtorch_wood", "[Diagnostics]", "DebugMode = false", "DebugVerbosity = Normal" })
                if (!text.Contains(setting)) throw new Exception("Missing default setting: " + setting);
            if (!setupMessages.Any(m => m.Contains("Created default configuration:")))
                throw new Exception("Creation was not logged.");
            System.Console.WriteLine("PASS: missing config directory and file created with every default and a creation log.");

            var reference = new Dictionary<ConfigDefinition, string>();
            string section = "";
            foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "heimkit.cfg")))
            {
                string value = line.Trim();
                if (value.StartsWith('#')) continue;
                if (value.StartsWith('[') && value.EndsWith(']')) { section = value[1..^1]; continue; }
                int equals = value.IndexOf('=');
                if (equals >= 0) reference.Add(new ConfigDefinition(section, value[..equals].Trim()), value[(equals + 1)..].Trim());
            }
            var complete = ConfigurationLoader.Load(Path.Combine(scratch, "complete"), setupLogger, config =>
            {
                _ = new InfiniteTorches(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new WeatherGuard(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new AFKNightWatch(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new AFKRaidWatch(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new SharedMap(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.DoorKeeper.DoorKeeper(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.ForestRenewal.ForestRenewal(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.SeaSalvage.SeaSalvage(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.TameTravel.TameTravel(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.HeimMender.HeimMender(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                return config;
            });
            if (complete.Count != reference.Count || complete.Any(entry =>
                !reference.TryGetValue(entry.Key, out var value) || value != entry.Value.GetSerializedValue()))
                throw new Exception("Reference configuration differs from the complete generated defaults.");
            System.Console.WriteLine("PASS: every reference setting matches generated defaults across all registered modules, with no hidden or retired keys.");
            AssertSharedFirst(File.ReadAllText(complete.ConfigFilePath));
            AssertSharedFirst(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "heimkit.cfg")));
            // Setting changes trigger BepInEx's native alphabetical save before the layout handler.
            complete[new ConfigDefinition("WeatherGuard", "Enabled")].BoxedValue = false;
            AssertSharedFirst(File.ReadAllText(complete.ConfigFilePath));
            var manuallyConfigured = File.ReadAllText(complete.ConfigFilePath).Replace("[WeatherGuard]", "[WeatherGuard]\nDisableDebugLogging = true");
            File.WriteAllText(complete.ConfigFilePath, manuallyConfigured);
            ConfigurationLoader.Load(Path.GetDirectoryName(complete.ConfigFilePath)!, setupLogger, config =>
            {
                _ = new InfiniteTorches(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new WeatherGuard(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new AFKNightWatch(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new AFKRaidWatch(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new SharedMap(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.DoorKeeper.DoorKeeper(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.ForestRenewal.ForestRenewal(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.SeaSalvage.SeaSalvage(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.TameTravel.TameTravel(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                _ = new HeimKit.Features.HeimMender.HeimMender(config, setupLogger, PlayerServicesTests.For(config, setupLogger));
                return config;
            });
            var reordered = File.ReadAllText(complete.ConfigFilePath);
            AssertSharedFirst(reordered);
            if (!reordered.Contains("DisableDebugLogging = true") || !reordered.Contains("# Default value: true") || !reordered.Contains("# Setting type: Boolean"))
                throw new Exception("Configuration layout lost manual values or generated descriptions.");
            var roundTrip = new ConfigFile(complete.ConfigFilePath, false) { SaveOnConfigSet = false };
            if (roundTrip.Bind("WeatherGuard", "Enabled", true).Value || !roundTrip.Bind("WeatherGuard", "DisableDebugLogging", false).Value)
                throw new Exception("Reordering changed stored feature values.");
            ConfigurationLayout.Apply(complete.ConfigFilePath);
            if (File.ReadAllText(complete.ConfigFilePath) != reordered) throw new Exception("Layout was not idempotent.");
            System.Console.WriteLine("PASS: shared config sections precede features on creation, reload and automatic saves, with reference parity and preserved manual values/descriptions.");

            // Simulate an existing user config that disables refilling and diagnostics and omits newer settings.
            File.WriteAllText(path, "[Torches]\nEnabled = false\nIntervalSeconds = 12\n\n[Diagnostics]\nDebugMode = true\nLogStatus = false\nLogRefillRequests = false\nLogRefillDetails = false\nStatusIntervalSeconds = 120\n");
            setupMessages.Clear();
            var reloaded = ConfigurationLoader.Load(directory, setupLogger, config => new InfiniteTorches(config, setupLogger, PlayerServicesTests.For(config, setupLogger)));
            reloaded.Initialize("1.0.20");
            text = File.ReadAllText(path);
            foreach (string retired in new[] { "LogStatus", "LogRefillRequests", "LogRefillDetails", "StatusIntervalSeconds" })
                if (text.Contains(retired)) throw new Exception("Retired diagnostic setting remains: " + retired);
            if (reloaded.IsActive || !text.Contains("Enabled = false") || !text.Contains("IntervalSeconds = 12") || !text.Contains("DebugMode = true") || !text.Contains("MaxMessagesPerPass = 32"))
                throw new Exception("Existing settings were reset or missing defaults were not added.");
            if (!setupMessages.Any(m => m.Contains("Loaded configuration:")) || setupMessages.Any(m => m.Contains("Created default configuration:")))
                throw new Exception("Reload was logged incorrectly.");
            System.Console.WriteLine("PASS: existing settings preserved, missing defaults added, disabled feature respected and reload logged.");

            // A file occupying the expected directory must fail clearly, without reporting successful setup.
            var blocked = Path.Combine(scratch, "blocked");
            File.WriteAllText(blocked, "This is a file, not a configuration directory.");
            setupMessages.Clear();
            bool failed = false;
            try { ConfigurationLoader.Load(blocked, setupLogger, config => new InfiniteTorches(config, setupLogger, PlayerServicesTests.For(config, setupLogger))); }
            catch (IOException) { failed = true; }
            if (!failed || !setupMessages.Any(m => m.Contains("Could not load or create configuration")) || setupMessages.Any(m => m.StartsWith("Created default configuration:")))
                throw new Exception("Configuration setup failure was not handled correctly.");
            System.Console.WriteLine("PASS: unwritable configuration location reports failure instead of successful creation.");
        }

        finally
        {
            // Only remove the isolated directory created by this test run.
            var resolvedScratch = Path.GetFullPath(scratch);
            var allowedRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolvedScratch.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Refusing cleanup outside the test output directory.");
            if (Directory.Exists(resolvedScratch)) Directory.Delete(resolvedScratch, true);
        }

    }
}
