using System;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorchesDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly ConfigEntry<bool> _debugMode;
    private const float DiagnosticIntervalSeconds = 60f;
    public const int RefillDetailLimit = 4;
    private bool _reported;
    private float _nextReport;
    private bool _detailsReported;
    private float _nextDetails;
    private int _detailCount;

    public InfiniteTorchesDiagnostics(ConfigFile config, ManualLogSource logger)
        : this(HeimKit.Configuration.DiagnosticsSettings.Bind(config), logger) { }

    public InfiniteTorchesDiagnostics(ConfigEntry<bool> debugMode, ManualLogSource logger)
    {
        _logger = logger;
        _debugMode = debugMode;
    }

    public bool IsDebugEnabled => _debugMode.Value;

    public string SettingsDescription => $"debug mode {_debugMode.Value}; automatic diagnostic interval {DiagnosticIntervalSeconds}s";

    public void LogRefillRequest(float now, Func<string> describe)
    {
        if (!_debugMode.Value) return;
        if (!_detailsReported || now >= _nextDetails)
        {
            _detailsReported = true;
            _nextDetails = now + DiagnosticIntervalSeconds;
            _detailCount = 0;
        }
        if (_detailCount >= RefillDetailLimit) return;
        _detailCount++;
        _logger.LogInfo("Infinite Torches refill request: " + describe());
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
        _detailCount = 0;
    }
}
