using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.IO;
using BepInEx.Logging;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardCoverageRequests
{
    public const string FileName = "heimkit-weatherguard-coverage.request";
    private readonly string _path;
    private readonly ManualLogSource _logger;
    private readonly ScheduleToken _nextPoll = new();
    private bool _warned;
    public WeatherGuardCoverageRequests(string configDirectory, ManualLogSource logger)
    { _path = Path.Combine(configDirectory, FileName); _logger = logger; }
    public void Poll(float now, Action report)
    {
        if (!_nextPoll.Ready(now)) return;
        _nextPoll.Postpone(now, 1);
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
                DiagnosticLog.Write(_logger, "WeatherGuard", "Could not consume coverage request; correct file access to retry", level: LogLevel.Warning);
            }
            return;
        }
        try { report(); }
        catch (Exception error)
        {
            DiagnosticLog.Write(_logger, "WeatherGuard", CoreLogTopics.Lifecycle, "Coverage request consumed but report failed; processing state unchanged", default, LogLevel.Error, error: error);
        }
    }
}
