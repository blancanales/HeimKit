using HeimKit.Configuration;
using HeimKit.Shared.Players;
using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;
namespace HeimKit.Features.ForestRenewal;
public sealed class ForestRenewal : IServerModule
{
    private readonly ForestRenewalSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
    private readonly PlayerServices _players;
    private ForestRenewalController? _controller;
    private IDisposable? _presenceRegistration;
    private bool _occupied;
    private ZDOMan? _world;
    private long _worldUid;
    private readonly string _directory;
    private readonly PlayerProcessingNotices _processing;
    public string Name => "ForestRenewal";
    public bool IsActive { get; private set; }
    public ForestRenewal(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(config.ConfigFilePath)!, "HeimKit", "ForestRenewal");
        _logger = logger;
        _compatibility = GameVersionCompatibility.Resolve(config, logger);
        _settings = new(config, logger);
        _players = players;
        _processing = new(logger, Name, () => _settings.DebugLogging.Enabled);
    }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        Log(_settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!_settings.Enabled.Value) return;
        if (!_compatibility.Supports(gameVersion)) { Log("DISABLED: unsupported game version " + gameVersion + "; required version " + _compatibility.RequiredVersion, LogLevel.Warning); return; }
        IsActive = true;
        if (_settings.DebugLogging.Enabled) Log("Waiting for joined players and dedicated-server world", LogLevel.Debug);
        try { _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail, () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady); }
        catch (Exception error) { Fail(error); }
        if (!IsActive) { var registration = _presenceRegistration; _presenceRegistration = null; registration?.Dispose(); }
    }
    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        try
        {
            bool ready = PlayerServices.Ready && ZNetScene.instance != null && ZoneSystem.instance != null && ZRoutedRpc.instance != null;
            if (ready) _processing.ServerReady();
            if (!_occupied) { StopController(); return; }
            if (!ready) { StopController(); _processing.Paused("world services unavailable"); return; }
            long uid = ZNet.instance.GetWorldUID();
            if (!ReferenceEquals(_world, ZDOMan.instance) || _worldUid != uid)
            { StopController(); _world = ZDOMan.instance; _worldUid = uid; }
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
        _controller = new(this, _settings, _logger, _players, _directory);
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
