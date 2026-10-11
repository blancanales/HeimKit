using System;
using BepInEx.Logging;
using HeimKit.Features.WeatherGuard.Models;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly bool _debug;
    private float _nextSummary, _sampleWindow;
    private int _samples;
    public const int SampleLimit = 6;

    public WeatherGuardDiagnostics(ManualLogSource logger, bool debug)
    { _logger = logger; _debug = debug; }

    public void Report(float now, Func<WeatherGuardStatus> snapshot)
    {
        if (!_debug || now < _nextSummary) return;
        _nextSummary = now + 60f;
        var status = snapshot();
        _logger.LogInfo($"WeatherGuard: status; protected={status.Protected}; eligible={status.Eligible}; waiting for context={status.Waiting}; waiting terrain={status.WaitingTerrain}; missing instances={status.WaitingInstance}; waiting surrounding objects={status.WaitingObjects}; regions={status.Regions}/{WeatherGuardRules.MaximumRegions}; nearby objects={status.Nearby}; distant objects={status.Distant}; acquired={status.Acquired}; released={status.Released}; blocked client ZDO records={status.BlockedData}; prevented owner transfers={status.BlockedOwner}; blocked client broadcasts={status.BlockedBroadcast}; owner commands routed={status.Commands}; protected UpdateWear calls={status.WearPasses}; capacity limited={status.Limited}. Live rain/combat/support verification pending.");
    }

    public void Sample(float now, Func<string> describe)
    {
        if (!_debug) return;
        if (now < _sampleWindow || now - _sampleWindow >= 60f)
        { _sampleWindow = now; _samples = 0; }
        if (_samples >= SampleLimit) return;
        _samples++;
        _logger.LogInfo("WeatherGuard: " + describe());
    }

    public void Reset()
    { _nextSummary = 0f; _sampleWindow = 0f; _samples = 0; }
}
