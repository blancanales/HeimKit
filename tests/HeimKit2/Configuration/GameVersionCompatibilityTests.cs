using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;

internal static class GameVersionCompatibilityTests
{
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    public static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "heimkit-version-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "heimkit.cfg");
        using var logger = new ManualLogSource("Version policy checks");
        var warnings = new List<string>();
        logger.LogEvent += (_, args) => { if (args.Level == LogLevel.Warning) warnings.Add(LogCapture.Text(args.Data)); };
        try
        {
            foreach (string? value in new string?[] { null, "true", "false", "TRUE", "garbage", "" })
            {
                warnings.Clear();
                File.WriteAllText(path, "[Compatibility]\nSupportedValheimVersion = 9.9.9\n" +
                    (value == null ? "" : "AllowUnverifiedVersions = " + value + "\n") + "[Torches]\nEnabled = false\n");
                var config = ConfigurationLoader.Load(folder, logger, cfg =>
                    (Config: cfg, Policy: GameVersionCompatibility.Resolve(cfg, logger)));
                bool allow = value == null || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                Check(config.Policy.AllowUnverifiedVersions == allow, "Boolean version policy default/preservation/validation failed.");
                Check(config.Policy.Supports(GameVersionCompatibility.AuditedVersion), "Audited version was rejected.");
                Check(!config.Policy.NeedsStartupBackup(GameVersionCompatibility.AuditedVersion), "Audited startup requested an upgrade backup.");
                foreach (string version in new[] { "0.0.0", "1.0.20", "1.0.22", "2.10.300", "9.9.9", "1.0.22-public-test", "1.0.22.1", "future-release" })
                {
                    Check(config.Policy.Supports(version) == allow, "Unverified version policy failed: " + version);
                    Check(config.Policy.NeedsStartupBackup(version) == (allow && (GameVersionCompatibility.CompareToAudited(version) ?? 1) > 0), "Upgrade backup requirement failed: " + version);
                }
                foreach (string version in new[] { "", " ", " 1.0.21 ", "1.0.21\nextra", new string('x', 129) })
                    Check(!config.Policy.Supports(version) && !config.Policy.NeedsStartupBackup(version), "Malformed observed version was accepted.");
                Check(warnings.Count == 1 || value == "false" && warnings.Count == 0,
                    "Policy must emit one shared unverified/invalid warning, independently of the number of modules.");
                string saved = File.ReadAllText(path);
                Check(saved.Contains("AllowUnverifiedVersions") && !saved.Contains("SupportedValheimVersion"), "Retired exact-version key survived migration or new switch was omitted.");
                Check(saved.Contains("Enabled = false"), "Migration changed unrelated feature settings.");
                Check(saved.IndexOf("[Compatibility]", StringComparison.Ordinal) < saved.IndexOf("[Torches]", StringComparison.Ordinal), "Generated compatibility section must precede features.");
            }
            File.WriteAllText(path, "[Compatibility]\nAllowUnverifiedVersions = true\n");
            var original = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var policy = GameVersionCompatibility.Resolve(original, logger);
            File.WriteAllText(path, "[Compatibility]\nAllowUnverifiedVersions = false\n");
            original.Reload();
            Check(ReferenceEquals(policy, GameVersionCompatibility.Resolve(original, logger)) && policy.AllowUnverifiedVersions,
                "Version policy changed without a new startup configuration session.");
            Check(!GameVersionCompatibility.Resolve(new ConfigFile(path, false), logger).AllowUnverifiedVersions, "Restart did not apply strict mode.");
            warnings.Clear();
            var locked = new ConfigFile(path, false) { SaveOnConfigSet = false };
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!GameVersionCompatibility.Resolve(locked, logger).AllowUnverifiedVersions, "Unreadable policy did not use strict mode.");
            Check(warnings.Count == 1, "Unreadable policy must warn once.");
        }
        finally { Directory.Delete(folder, true); }
        System.Console.WriteLine("PASS: default-on unverified versions, strict mode, generated configuration, retired-key migration, one shared warning, invalid-version rejection and restart semantics.");
    }
}
