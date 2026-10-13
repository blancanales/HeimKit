using HeimKit.Features.AFKRaidWatch.Models;
using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;

using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;
namespace HeimKit.Features.AFKRaidWatch;
public sealed class AFKRaidWatch : IServerModule
{
    private readonly AFKRaidWatchSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly PlayerServices _players;
    private AFKRaidWatchController? _controller;
    private IDisposable? _presenceRegistration;
    private bool _occupied;
    private readonly PlayerProcessingNotices _processing;
    public string Name => "AFKRaidWatch";
    public bool IsActive { get; private set; }
    public AFKRaidWatch(ConfigFile config, ManualLogSource logger, PlayerServices players) { _logger = logger; _settings = new(config, logger); _players = players; _processing = new(logger, Name, () => _settings.DebugLogging.Enabled); }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        Log(_settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!_settings.Enabled.Value) return;
        if (gameVersion != "1.0.17") { Log("DISABLED: unsupported game version " + gameVersion, LogLevel.Warning); return; }
        IsActive = true;
        if (_settings.DebugLogging.Enabled) Log("Waiting for joined players and dedicated-server world", LogLevel.Debug);
        _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail, () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady);
    }
    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        try
        {
            if (PlayerServices.Ready) _processing.ServerReady();
            if (!_occupied) { StopController(); return; }
            if (!PlayerServices.Ready) { StopController(); _processing.Paused("world services unavailable"); return; }
            StartController();
            _controller?.Tick(now);
        }
        catch (Exception error) { Fail(error); }
    }
    private void PresenceChanged(bool? occupied)
    {
        _occupied = occupied == true;
        if (!IsActive) return;
        if (!_occupied)
        {
            StopController();
            if (occupied.HasValue) _processing.EmptyPaused();
            else _processing.Unavailable("player presence unavailable");
            return;
        }
        // Activation runs in Tick after server readiness is announced.
    }
    private void StartController()
    {
        if (!_settings.Enabled.Value || _controller != null) return;
        _controller = new(this, _settings, _logger, _players);
        _processing.Ready();
    }
    private void StopController()
    {
        var controller = _controller; _controller = null;
        controller?.Dispose();
    }
    internal void Fail(Exception error) { if (!IsActive) return; Log("DISABLED after runtime failure: " + error, LogLevel.Error); Shutdown(); }
    private void Log(string message, LogLevel level = LogLevel.Info) => DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, message, level: level);
    public void Shutdown()
    {
        IsActive = false; _occupied = false; _processing.Reset();
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController);
    }
}
