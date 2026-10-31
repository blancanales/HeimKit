using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;

internal static class GameVersionCompatibilityTests
{
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }

    public static void Run()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "version-policy-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "heimkit.cfg");
        using var logger = new ManualLogSource("Manual game version tests");
        var warnings = new List<string>();
        logger.LogEvent += (_, args) => { if (args.Level == LogLevel.Warning) warnings.Add(LogCapture.Text(args.Data)); };
        try
        {
            foreach (string? value in new string?[] { null, "1.0.20", "1.0.21", " 1.0.21 ", "0.0.0", "2.10.300", "", "*", "1.0.*", "1.0", "1.0.21.0", "01.0.21", "1.0.021", "+1.0.21", "1.0.-1", "1.0.21-beta", "1.0.20,1.0.21", "2147483648.0.0" })
            {
                warnings.Clear();
                File.WriteAllText(path, "[Torches]\nEnabled = true\n" + (value == null ? "" : "[Compatibility]\nSupportedValheimVersion = " + value + "\n"));
                var loaded = ConfigurationLoader.Load(folder, logger, config =>
                {
                    var players = PlayerServicesTests.For(config, logger);
                    IServerModule[] modules = { new InfiniteTorches(config, logger, players), new WeatherGuard(config, logger, players),
                        new AFKNightWatch(config, logger, players), new AFKRaidWatch(config, logger, players) };
                    return (Config: config, Modules: modules, Policy: GameVersionCompatibility.Resolve(config, logger));
                });
                string expected = value?.Trim() switch { "1.0.20" => "1.0.20", "1.0.21" => "1.0.21", "0.0.0" => "0.0.0", "2.10.300" => "2.10.300", _ => GameVersionCompatibility.AuditedVersion };
                bool invalid = value != null && value.Trim() != expected;
                Check(loaded.Policy.RequiredVersion == expected, "Version override validation/fallback: " + value);
                Check(warnings.Count == (value == null || value.Trim() == GameVersionCompatibility.AuditedVersion ? 0 : 1), "Shared policy must warn only once across all modules.");
                if (invalid) Check(warnings.Single().Contains("Invalid"), "Malformed version must report fallback.");
                else if (expected != GameVersionCompatibility.AuditedVersion)
                    Check(warnings.Single().Contains("does not validate gameplay compatibility"), "Manual requirement must not claim an audit.");
                try
                {
                    foreach (var module in loaded.Modules)
                    {
                        module.Initialize(expected);
                        Check(module.IsActive, module.Name + " did not accept the effective exact requirement.");
                        module.Initialize("9.9.9");
                        Check(!module.IsActive, module.Name + " treated override as a blanket bypass.");
                        module.Initialize(GameVersionCompatibility.AuditedVersion);
                        Check(module.IsActive == (expected == GameVersionCompatibility.AuditedVersion), module.Name + " treated override as an additional allowed version.");
                    }
                }
                finally { foreach (var module in loaded.Modules) module.Shutdown(); }
                loaded.Config.Save();
                string saved = File.ReadAllText(path);
                Check(saved.Contains("SupportedValheimVersion") == (value != null), "Hidden key generated or lost.");
                Check(!loaded.Config.ContainsKey(new ConfigDefinition("Compatibility", "SupportedValheimVersion")), "Manual entry should remain unbound.");
                if (value != null)
                {
                    Check(saved.IndexOf("[Compatibility]", StringComparison.Ordinal) < saved.IndexOf("[Torches]", StringComparison.Ordinal), "Manual shared section must precede features.");
                    var reloaded = new ConfigFile(path, false) { SaveOnConfigSet = false };
                    Check(reloaded.Bind("Compatibility", "SupportedValheimVersion", "sentinel").Value == value.Trim(), "Manual value rewritten during save.");
                    Check(GameVersionCompatibility.Resolve(new ConfigFile(path, false), logger).RequiredVersion == expected, "Saved override did not survive reload.");
                }
            }

            File.WriteAllText(path, "[Compatibility]\nSupportedValheimVersion = 1.0.22\nSupportedValheimVersion = 1.0.21\n");
            var original = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var policy = GameVersionCompatibility.Resolve(original, logger);
            Check(policy.RequiredVersion == "1.0.21", "Duplicate entries must match loader last-value-wins behavior.");
            File.WriteAllText(path, "[Compatibility]\nSupportedValheimVersion = 1.0.22\n");
            original.Reload();
            Check(ReferenceEquals(policy, GameVersionCompatibility.Resolve(original, logger)) && policy.RequiredVersion == "1.0.21", "Policy must remain fixed until a new configuration session.");
            Check(GameVersionCompatibility.Resolve(new ConfigFile(path, false), logger).RequiredVersion == "1.0.22", "New configuration must observe updated requirement.");

            warnings.Clear();
            var unreadable = new ConfigFile(path, false) { SaveOnConfigSet = false };
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(GameVersionCompatibility.Resolve(unreadable, logger).RequiredVersion == GameVersionCompatibility.AuditedVersion, "Unreadable override must fall back.");
            Check(warnings.Count == 1 && warnings[0].Contains("Could not read"), "Unreadable override must warn once.");

            File.WriteAllText(path, "[Compatibility]\nSupportedValheimVersion = 1.0.21\n[Torches]\nEnabled = false\n[WeatherGuard]\nEnabled = false\n[AFKNightWatch]\nEnabled = false\n[AFKRaidWatch]\nEnabled = false\n");
            var disabled = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var disabledPlayers = PlayerServicesTests.For(disabled, logger);
            IServerModule[] disabledModules = { new InfiniteTorches(disabled, logger, disabledPlayers), new WeatherGuard(disabled, logger, disabledPlayers),
                new AFKNightWatch(disabled, logger, disabledPlayers), new AFKRaidWatch(disabled, logger, disabledPlayers) };
            try { foreach (var module in disabledModules) { module.Initialize("1.0.21"); Check(!module.IsActive, "Override must not enable a disabled module."); } }
            finally { foreach (var module in disabledModules) module.Shutdown(); }
        }
        finally { Directory.Delete(folder, true); }
        System.Console.WriteLine("PASS: hidden exact-version override across all modules, strict validation/fallback, one shared warning, default omission, manual save/layout preservation, disabled modules, mismatch rejection and restart semantics.");
    }
}
