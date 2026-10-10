using System;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HeimKit.Features.InfiniteTorches;

public sealed class TorchDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly ConfigEntry<bool> _debugMode;
    private const float DiagnosticIntervalSeconds = 60f;
    private bool _reported;
    private float _nextReport;
    private bool _detailsReported;
    private float _nextDetails;

    public TorchDiagnostics(ConfigFile config, ManualLogSource logger)
    {
        _logger = logger;
        _debugMode = config.Bind("Diagnostics", "DebugMode", false,
            "Enable troubleshooting diagnostics. When false, only standard startup, readiness, warning and error logs are emitted.");
        // Consume and remove retired settings so existing files also show just the one debug switch.
        foreach (string key in new[] { "LogStatus", "StatusIntervalSeconds", "LogRefillDetails", "LogRefillRequests" })
        {
            config.Bind("Diagnostics", key, "");
            config.Remove(new ConfigDefinition("Diagnostics", key));
        }
    }

    public bool IsDebugEnabled => _debugMode.Value;

    public string SettingsDescription => $"debug mode {_debugMode.Value}; automatic diagnostic interval {DiagnosticIntervalSeconds}s";

    public bool BeginRefillDetails(float now)
    {
        if (!_debugMode.Value || (_detailsReported && now < _nextDetails)) return false;
        _detailsReported = true;
        _nextDetails = now + DiagnosticIntervalSeconds;
        return true;
    }

    public void Report(float now, Func<string> describe)
    {
        if (!_debugMode.Value || (_reported && now < _nextReport)) return;
        _logger.LogInfo("Infinite Torches status: " + describe());
        _reported = true;
        _nextReport = now + DiagnosticIntervalSeconds;
    }

    public void Reset()
    {
        _reported = false;
        _nextReport = 0f;
        _detailsReported = false;
        _nextDetails = 0f;
    }
}
