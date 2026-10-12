using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;
using HeimKit.Features.AFKNightWatch.Models;
using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;
namespace HeimKit.Features.AFKNightWatch;
public sealed class AFKNightWatch : IServerModule
{
    private readonly AFKNightWatchSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly PlayerServices _players;
    private AFKNightWatchController? _controller;
    private IDisposable? _presenceRegistration;
    private bool _occupied;
    public string Name => "AFKNightWatch";
    public bool IsActive { get; private set; }
    public AFKNightWatch(ConfigFile config, ManualLogSource logger, PlayerServices players) { _logger = logger; _settings = new(config, logger); _players = players; }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        Log(_settings.Enabled.Value ? "Enabled." : "Disabled.");
        if (!_settings.Enabled.Value) return;
        if (!_settings.MajoritySleepEnabled.Value && !_settings.ExcludeAFKFromSleep.Value) { Log("No behaviors are selected; feature will remain inactive.", LogLevel.Warning); return; }
        if (gameVersion != "1.0.17") { Log("DISABLED: unsupported game version " + gameVersion, LogLevel.Warning); return; }
        IsActive = true;
        if (_settings.DebugLogging.Enabled) Log("Waiting for connected players and dedicated-server world", LogLevel.Debug);
        _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail);
    }
    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        try
        {
            if (!_occupied || !PlayerServices.Ready) { StopController(); return; }
            StartController();
            _controller?.Tick(now);
        }
        catch (Exception error) { Fail(error); }
    }
    private void PresenceChanged(bool? occupied)
    {
        _occupied = occupied == true;
        if (!IsActive) return;
        if (!_occupied) { StopController(); return; }
        if (PlayerServices.Ready) StartController();
    }
    private void StartController()
    {
        if (!_settings.Enabled.Value || _controller != null) return;
        _controller = new(this, _settings, _logger, _players);
        Log("Ready.");
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
        IsActive = false; _occupied = false;
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController);
    }
}
