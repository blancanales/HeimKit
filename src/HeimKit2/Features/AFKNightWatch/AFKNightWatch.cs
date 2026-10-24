using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Configuration;
using HeimKit.Core.Players;
using HeimKit.Core.Players.Models;
using HeimKit.Features.AFKNightWatch.Models;
using System;
using BepInEx.Configuration;
using BepInEx.Logging;
namespace HeimKit.Features.AFKNightWatch;
public sealed class AFKNightWatch : IServerModule
{
    private readonly AFKNightWatchSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
    private readonly PlayerServices _players;
    private AFKNightWatchController? _controller;
    private IDisposable? _presenceRegistration;
    private bool _occupied;
    private readonly PlayerProcessingNotices _processing;
    public string Name => "AFKNightWatch";
    public bool IsActive { get; private set; }
    public AFKNightWatch(ConfigFile config, ManualLogSource logger, PlayerServices players) { _logger = logger; _compatibility = GameVersionCompatibility.Resolve(config, logger); _settings = new(config, logger); _players = players; _processing = new(logger, Name, () => _settings.DebugLogging.Enabled); }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        Log(_settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!_settings.Enabled.Value) return;
        if (!_settings.MajoritySleepEnabled.Value && !_settings.ExcludeAFKFromSleep.Value) { Log("No behaviors are selected; feature will remain inactive.", LogLevel.Warning); return; }
        if (!_compatibility.Supports(gameVersion)) { Log("DISABLED: unsupported game version " + gameVersion + "; required version " + _compatibility.RequiredVersion, LogLevel.Warning); return; }
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
        var controller = _controller; _controller = null; if (controller != null) ModuleScheduler.Current.Forget(controller);
        try { controller?.Dispose(); } finally { WorldObjectProvider.Current?.ReleaseOwner(Name); }
    }
    internal void Fail(Exception error) { if (!IsActive) return; DiagnosticLog.Write(_logger, Name, CoreLogTopics.Lifecycle, "Disabled after a runtime failure.", level: LogLevel.Error, error: error); Shutdown(); }
    private void Log(string message, LogLevel level = LogLevel.Info) => DiagnosticLog.Write(_logger, Name, message, level: level);
    public void Shutdown()
    {
        IsActive = false; _occupied = false; _processing.Reset();
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController);
    }
}
