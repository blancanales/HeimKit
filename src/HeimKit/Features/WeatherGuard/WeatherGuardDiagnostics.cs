using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Features.WeatherGuard.Models;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuardDiagnostics
{
    private readonly ManualLogSource _logger;
    private readonly bool _debug;
    private float _nextSummary;
    private readonly Dictionary<string, (float Start, int Count)> _streams = new();
    private readonly Dictionary<bool, (float Start, HashSet<string> Ids)> _wearStreams = new();
    public const int SampleLimit = 6;
    public const int WearSampleLimit = 3;
    internal bool DebugEnabled => _debug;

    public WeatherGuardDiagnostics(ManualLogSource logger, bool debug)
    { _logger = logger; _debug = debug; }

    public void Report(float now, Func<WeatherGuardStatus> snapshot)
    {
        if (!_debug || now < _nextSummary) return;
        _nextSummary = now + 60f;
        var status = snapshot();
        _logger.LogInfo($"WeatherGuard: status; protected={status.Protected}; eligible={status.Eligible}; waiting for context={status.Waiting}; waiting terrain={status.WaitingTerrain}; missing instances={status.WaitingInstance}; waiting surrounding objects={status.WaitingObjects}; regions={status.Regions}/{WeatherGuardRules.MaximumRegions}; nearby objects={status.Nearby}; distant objects={status.Distant}; acquired={status.Acquired}; released={status.Released}; blocked client ZDO records={status.BlockedData}; prevented owner transfers={status.BlockedOwner}; blocked client broadcasts={status.BlockedBroadcast}; owner commands routed={status.Commands}; protected UpdateWear calls={status.WearPasses}; capacity limited={status.Limited}. Live rain/combat/support verification pending.");
        _logger.LogInfo($"WeatherGuard: evidence summary; current scan peers={status.Peers}; ready peers={status.ReadyPeers}; waiting character={status.CharacterWaits}; exclusions=[{Counts(status.Exclusions)}]; releases this scan=[{Counts(status.ReleaseReasons)}]; cumulative ownership losses={status.OwnerLosses}; cumulative protected weather gates={status.WeatherGates}; cumulative wet opportunities={status.WetOpportunities}; native commands received={status.NativeCommands}; native support passes={status.SupportPasses}. Gates prove entry past owner/warmup checks; wet opportunities identify native wet/health/shield conditions, not elapsed rain damage or client delivery.");
    }
    private static string Counts(Dictionary<string, int> values)
        => string.Join(", ", values.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    public void Sample(float now, Func<string> describe)
        => Sample(now, "acquisition", describe);
    public void Sample(float now, string stream, Func<string> describe)
    {
        if (!_debug) return;
        if (!Reserve(now, stream)) return;
        _logger.LogInfo("WeatherGuard: " + describe());
    }
    private bool Reserve(float now, string stream)
    {
        if (!_debug) return false;
        if (!_streams.TryGetValue(stream, out var window) || now < window.Start || now - window.Start >= 60f)
            window = (now, 0);
        if (window.Count >= SampleLimit) return false;
        _streams[stream] = (window.Start, window.Count + 1);
        return true;
    }
    public bool ReserveCommand(float now) => Reserve(now, "native command");
    public void Command(WearObservation observation, float? after, bool failed)
    {
        if (!_debug) return;
        _logger.LogInfo($"WeatherGuard: native command evidence; {observation.Object}; {observation.Context}; health before={observation.Before}; health after={(after.HasValue ? after.Value.ToString() : "object destroyed/invalid")}; exception={failed}. Receiver invocation observed; unchanged health does not prove a missed request, and resource drops require client observation.");
    }
    public bool TryWearSample(float now, string id, bool protectedPiece)
    {
        if (!_debug) return false;
        if (!_wearStreams.TryGetValue(protectedPiece, out var window) || now < window.Start || now - window.Start >= 60f)
            window = (now, new HashSet<string>());
        if (window.Ids.Count >= WearSampleLimit || !window.Ids.Add(id)) return false;
        _wearStreams[protectedPiece] = window;
        return true;
    }
    public void Wear(WearObservation observation, float? after, bool failed)
    {
        if (!_debug) return;
        _logger.LogInfo($"WeatherGuard: wear evidence; {observation.Object}; protected={observation.Protected}; local owner={observation.Owner}; warmup={observation.Warmup}; weather gate reached={observation.GateReached}; weather damage suppressed={observation.Suppressed}; health before={observation.Before}; health after={(after.HasValue ? after.Value.ToString() : "object destroyed/invalid")}; exception={failed}; {observation.Context}. Server observations only; health changes do not identify their cause. Unowned controls are processed by clients, whose weather/timers are not observable here.");
    }

    public void Reset()
    { _nextSummary = 0f; _streams.Clear(); _wearStreams.Clear(); }
}
