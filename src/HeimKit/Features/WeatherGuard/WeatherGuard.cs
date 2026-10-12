using System;
using System.IO;
using HeimKit.Features.WeatherGuard.Models;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;
using HeimKit.Shared.Players;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuard : IServerModule
{
    private readonly WeatherGuardSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly string _configPath;
    private readonly WeatherGuardCoverageRequests _coverageRequests;
    private readonly WeatherGuardDiagnostics _adminDiagnostics;
    private WeatherGuardController? _controller;
    private ZDOMan? _world;
    private readonly PlayerServices _players;
    private IDisposable? _presenceRegistration;
    private bool? _occupied;
    public string Name => "WeatherGuard";
    public bool IsActive { get; private set; }

    public WeatherGuard(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _logger = logger;
        _players = players;
        _settings = new WeatherGuardSettings(config, logger);
        _configPath = config.ConfigFilePath;
        _coverageRequests = new WeatherGuardCoverageRequests(Path.GetDirectoryName(_configPath)!, logger);
        _adminDiagnostics = new WeatherGuardDiagnostics(logger, false);
    }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, _settings.Enabled.Value ? "Enabled." : "Disabled.");
        if (!_settings.Enabled.Value)
        { return; }
        if (gameVersion != "1.0.17")
        { DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, $"DISABLED (unsupported Valheim version {gameVersion}; audited for 1.0.17).", level: LogLevel.Warning); return; }
        IsActive = true;
        if (_settings.DebugLogging.Enabled)
        {
            DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, "Waiting for connected players and dedicated-server world", new (string, object?)[] { ("game_version", gameVersion), ("config", _configPath), ("debug_mode", _settings.DebugLogging.Enabled), ("feature_debug_mode", _settings.DebugMode.Value), ("global_debug_mode", _settings.GlobalDebugMode.Value), ("debug_suppressed", _settings.DebugLogging.Suppressed), ("rain_protection", _settings.ProtectRain.Value), ("underwater_protection", _settings.ProtectUnderwater.Value), ("debug_log_level", _settings.DebugLogLevel.Value), ("log_world_created_items", _settings.LogWorldCreatedItems.Value), ("log_schema", 1) }, LogLevel.Debug);
            DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, _settings.ProtectRain.Value || _settings.ProtectUnderwater.Value
                ? "player-built structures use server processing when surrounding terrain and objects are ready. Existing creators are preserved. No logout or repair required. World-created claims are inactive."
                : "Rain and underwater protections are both disabled; no pieces are acquired. Coverage reports remain available.", level: LogLevel.Debug);
        }
        _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail);
    }
    public void Tick(float now)
    {
        if (IsActive && !_settings.Enabled.Value) Shutdown();
        if (IsActive)
        {
            if (_occupied != true) StopController();
            else
            {
                StartController();
                _controller?.Tick(now);
            }
        }
        PollCoverage(now);
    }
    private void PresenceChanged(bool? occupied)
    {
        _occupied = occupied;
        if (!IsActive) return;
        if (occupied != true) StopController();
        else if (_settings.Enabled.Value) StartController();
    }
    private void StartController()
    {
        var net = ZNet.instance; var world = ZDOMan.instance;
        if (net == null || world == null || ZNetScene.instance == null || ZoneSystem.instance == null || ZRoutedRpc.instance == null ||
            !net.IsServer() || !net.IsDedicated()) { StopController(); return; }
        if (!ReferenceEquals(_world, world)) StopController();
        if (_controller != null) return;
        _world = world;
        _controller = new WeatherGuardController(this, _logger, _settings.DebugLogging.ConfiguredEnabled, _settings.DebugLogLevel.Value, _settings.LogWorldCreatedItems.Value, _settings.ProtectRain.Value, _settings.ProtectUnderwater.Value, _settings.ExcludedPrefabs.Value, _players.Presence);
        _controller.Install();
        DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, "Ready.");
    }
    private void StopController()
    {
        var controller = _controller; _controller = null; _world = null;
        controller?.Dispose();
    }
    private void PollCoverage(float now) => _coverageRequests.Poll(now, () =>
        _adminDiagnostics.Coverage((_controller != null && ReferenceEquals(_world, ZDOMan.instance) && ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated() && ZNetScene.instance != null && ZoneSystem.instance != null ? _controller.Coverage(now) : null) ?? new CoverageSnapshot
        { State = !IsActive ? "module_inactive" : _occupied == false ? "server_empty" : _occupied == null ? "player_presence_unavailable" : "waiting_for_world", RainEnabled = _settings.ProtectRain.Value, UnderwaterEnabled = _settings.ProtectUnderwater.Value }));
    internal void Fail(Exception error)
    {
        DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, $"DISABLED after controller error; returning pieces to vanilla ownership: {error}", level: LogLevel.Error);
        Shutdown();
    }
    public void Shutdown()
    {
        IsActive = false; _occupied = null;
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController);
    }
}
