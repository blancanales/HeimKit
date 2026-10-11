using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Configuration;

internal static class ConfigurationLoaderTests
{
    public static void Run()
    {
        // Use a new isolated directory to exercise real file creation and reload, not just in-memory settings.
        var scratch = Path.Combine(AppContext.BaseDirectory, "config-check-" + Guid.NewGuid());
        var directory = Path.Combine(scratch, "BepInEx", "config");
        var path = Path.Combine(directory, "heimkit.cfg");
        var setupMessages = new List<string>();
        using var setupLogger = new ManualLogSource("First-use config checks");
        setupLogger.LogEvent += (_, args) => setupMessages.Add(args.Data.ToString()!);
        try
        {
            ConfigurationLoader.Load(directory, setupLogger, config => new InfiniteTorches(config, setupLogger));
            if (!File.Exists(path)) throw new Exception("First-use config was not created in the correct folder.");
            var text = File.ReadAllText(path);
            foreach (string setting in new[] { "[Torches]", "Enabled = true", "IntervalSeconds = 5", "MaxMessagesPerPass = 32", "Prefabs = piece_groundtorch_wood", "[Diagnostics]", "DebugMode = false" })
                if (!text.Contains(setting)) throw new Exception("Missing default setting: " + setting);
            if (!setupMessages.Any(m => m.Contains("Created default configuration:")))
                throw new Exception("Creation was not logged.");
            System.Console.WriteLine("PASS: missing config directory and file created with every default and a creation log.");

            // Simulate an existing user config that disables refilling and diagnostics and omits newer settings.
            File.WriteAllText(path, "[Torches]\nEnabled = false\nIntervalSeconds = 12\n\n[Diagnostics]\nDebugMode = true\nLogStatus = false\nLogRefillRequests = false\nLogRefillDetails = false\nStatusIntervalSeconds = 120\n");
            setupMessages.Clear();
            var reloaded = ConfigurationLoader.Load(directory, setupLogger, config => new InfiniteTorches(config, setupLogger));
            reloaded.Initialize("1.0.17");
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
            try { ConfigurationLoader.Load(blocked, setupLogger, config => new InfiniteTorches(config, setupLogger)); }
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
