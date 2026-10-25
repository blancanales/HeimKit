using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HeimKit.Configuration;

public sealed class GameVersionCompatibility
{
    public const string AuditedVersion = "1.0.20";
    private const string Section = "Compatibility";
    private const string Key = "SupportedValheimVersion";
    private static readonly ConditionalWeakTable<ConfigFile, GameVersionCompatibility> Policies = new();
    public string RequiredVersion { get; }

    private GameVersionCompatibility(ConfigFile config, ManualLogSource logger)
    {
        RequiredVersion = AuditedVersion;
        string? value;
        try { value = Read(config); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            Warn(logger, "Could not read Compatibility.SupportedValheimVersion; using built-in Valheim version " + AuditedVersion + ".");
            return;
        }
        if (value == null) return;
        if (value.Length > 32 || !System.Version.TryParse(value, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0 ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            Warn(logger, "Invalid Compatibility.SupportedValheimVersion; enter one exact major.minor.patch version. Using built-in Valheim version " + AuditedVersion + ".");
            return;
        }
        RequiredVersion = value;
        if (RequiredVersion != AuditedVersion)
            Warn(logger, "Manual Valheim version requirement is " + RequiredVersion + "; built-in audited version is " + AuditedVersion + ". This override does not validate gameplay compatibility; runtime API checks remain active.");
    }

    public static GameVersionCompatibility Resolve(ConfigFile config, ManualLogSource logger) =>
        Policies.GetValue(config, current => new GameVersionCompatibility(current, logger));

    public bool Supports(string gameVersion) => string.Equals(gameVersion, RequiredVersion, StringComparison.Ordinal);

    private static void Warn(ManualLogSource logger, string message) =>
        DiagnosticLog.Write(logger, "HeimKit", message, level: LogLevel.Warning);

    private static string? Read(ConfigFile config)
    {
        var definition = new ConfigDefinition(Section, Key);
        if (config.ContainsKey(definition)) return config[definition].GetSerializedValue().Trim();
        if (!File.Exists(config.ConfigFilePath)) return null;
        // Match ConfigFile.Reload parsing without binding or generating the hidden entry.
        string currentSection = "";
        string? value = null;
        foreach (string line in File.ReadLines(config.ConfigFilePath))
        {
            string text = line.Trim();
            if (text.StartsWith("#")) continue;
            if (text.StartsWith("[") && text.EndsWith("]"))
            { currentSection = text.Substring(1, text.Length - 2); continue; }
            int equals = text.IndexOf('=');
            if (equals >= 0 && currentSection == Section && text.Substring(0, equals).Trim() == Key)
                value = text.Substring(equals + 1).Trim();
        }
        return value;
    }
}
