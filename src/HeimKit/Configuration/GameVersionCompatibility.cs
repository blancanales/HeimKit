using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;

namespace HeimKit.Configuration;

public sealed class GameVersionCompatibility
{
    public const string AuditedVersion = "1.0.21";
    private const string Section = "Compatibility";
    private const string Key = "AllowUnverifiedVersions";
    private static readonly ConditionalWeakTable<ConfigFile, GameVersionCompatibility> Policies = new();
    private readonly ManualLogSource _logger;
    private bool _reportedUnverified;
    public string RequiredVersion => AuditedVersion;
    public bool AllowUnverifiedVersions { get; }

    private GameVersionCompatibility(ConfigFile config, ManualLogSource logger)
    {
        _logger = logger;
        bool invalid = false;
        try
        {
            string? raw = Read(config, Key);
            invalid = raw != null && !bool.TryParse(raw, out _);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            invalid = true;
        }
        var entry = config.Bind(Section, Key, true,
            "Allow other Valheim versions. Newer versions require a recorded world upgrade backup once per world and HeimKit/Valheim version pair. Runtime feature checks remain active. False requires the audited game version. Restart after edits.");
        if (invalid)
        {
            entry.Value = false;
            Warn("Invalid or unreadable Compatibility.AllowUnverifiedVersions; using strict audited-version mode.");
        }
        AllowUnverifiedVersions = entry.Value;
        // Binding consumes BepInEx's orphaned value before removal, so the retired key is not saved again.
        var retired = config.Bind(Section, "SupportedValheimVersion", "");
        if (!string.IsNullOrEmpty(retired.Value))
            DiagnosticLog.Write(logger, "HeimKit", "Removed the retired SupportedValheimVersion setting; Compatibility.AllowUnverifiedVersions controls version policy.");
        config.Remove(new ConfigDefinition(Section, "SupportedValheimVersion"));
    }

    public static GameVersionCompatibility Resolve(ConfigFile config, ManualLogSource logger) =>
        Policies.GetValue(config, current => new GameVersionCompatibility(current, logger));

    public bool Supports(string gameVersion)
    {
        if (!HasObservedVersion(gameVersion)) return false;
        if (gameVersion == AuditedVersion) return true;
        if (!AllowUnverifiedVersions) return false;
        if (!_reportedUnverified)
        {
            _reportedUnverified = true;
            Warn("AllowUnverifiedVersions permits Valheim " + gameVersion + "; audited baseline is " + AuditedVersion +
                ". Newer server versions require a recorded world upgrade backup; runtime feature checks remain active.");
        }
        return true;
    }

    public bool NeedsStartupBackup(string gameVersion) =>
        AllowUnverifiedVersions && HasObservedVersion(gameVersion) &&
        (CompareToAudited(gameVersion) is int comparison ? comparison > 0 : gameVersion != AuditedVersion);

    // Compare numeric release components, accepting suffixes used by native prerelease builds.
    // If a future naming scheme cannot be ordered, require the same one-time checkpoint conservatively.
    public static int? CompareToAudited(string gameVersion)
    {
        if (!HasObservedVersion(gameVersion)) return null;
        int length = 0;
        while (length < gameVersion.Length && (gameVersion[length] >= '0' && gameVersion[length] <= '9' || gameVersion[length] == '.')) length++;
        if (!System.Version.TryParse(gameVersion.Substring(0, length).TrimEnd('.'), out var observed) || observed.Minor < 0) return null;
        var audited = System.Version.Parse(AuditedVersion);
        return new System.Version(observed.Major, observed.Minor, Math.Max(0, observed.Build), Math.Max(0, observed.Revision))
            .CompareTo(new System.Version(audited.Major, audited.Minor, Math.Max(0, audited.Build), Math.Max(0, audited.Revision)));
    }

    private static bool HasObservedVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim()) return false;
        foreach (char character in value) if (char.IsControl(character)) return false;
        return true;
    }

    private void Warn(string message) => DiagnosticLog.Write(_logger, "HeimKit", message, level: LogLevel.Warning);

    private static string? Read(ConfigFile config, string key)
    {
        var definition = new ConfigDefinition(Section, key);
        if (config.ContainsKey(definition)) return config[definition].GetSerializedValue().Trim();
        if (!File.Exists(config.ConfigFilePath)) return null;
        string currentSection = "";
        string? value = null;
        foreach (string line in File.ReadLines(config.ConfigFilePath))
        {
            string text = line.Trim();
            if (text.StartsWith("#")) continue;
            if (text.StartsWith("[") && text.EndsWith("]")) { currentSection = text.Substring(1, text.Length - 2); continue; }
            int equals = text.IndexOf('=');
            if (equals >= 0 && currentSection == Section && text.Substring(0, equals).Trim() == key)
                value = text.Substring(equals + 1).Trim();
        }
        return value;
    }
}
