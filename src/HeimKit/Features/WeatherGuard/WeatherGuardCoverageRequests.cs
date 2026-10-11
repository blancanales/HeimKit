using System;
using System.IO;
using BepInEx.Logging;
using HeimKit.Shared;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardCoverageRequests
{
    public const string FileName = "heimkit-weatherguard-coverage.request";
    private readonly string _path;
    private readonly ManualLogSource _logger;
    private float _nextPoll;
    private bool _warned;
    public WeatherGuardCoverageRequests(string configDirectory, ManualLogSource logger)
    { _path = Path.Combine(configDirectory, FileName); _logger = logger; }
    public void Poll(float now, Action report)
    {
        if (now < _nextPoll) return;
        _nextPoll = now + 1;
        try
        {
            if (!File.Exists(_path)) return;
            File.Delete(_path);
            _warned = false;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            if (!_warned)
            {
                _warned = true;
                DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle,
                    "Could not consume coverage request; correct file access to retry", new (string, object?)[] { ("request_path", _path) }, LogLevel.Warning);
            }
            return;
        }
        try { report(); }
        catch (Exception error)
        {
            DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle,
                "Coverage request consumed but report failed; processing state unchanged",
                new (string, object?)[] { ("error", error.ToString()) }, LogLevel.Error);
        }
    }
}
