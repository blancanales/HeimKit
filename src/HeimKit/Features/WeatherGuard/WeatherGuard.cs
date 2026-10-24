using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Configuration;
using System;
using System.IO;
using HeimKit.Features.WeatherGuard.Models;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Core.Players;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuard : IServerModule
{
    private readonly WeatherGuardSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
    private readonly string _configPath;
    private readonly WeatherGuardCoverageRequests _coverageRequests;
    private readonly WeatherGuardDiagnostics _adminDiagnostics;
    private WeatherGuardController? _controller;
    private ZDOMan? _world;
    private readonly PlayerServices _players;
    private IDisposable? _presenceRegistration;
    private bool? _occupied;
    private readonly PlayerProcessingNotices _processing;
    public string Name => "WeatherGuard";
    public bool IsActive { get; private set; }

    public WeatherGuard(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _coverageReport = ReportCoverage;
        _logger = logger; _compatibility = GameVersionCompatibility.Resolve(config, logger);
        _players = players;
        _settings = new WeatherGuardSettings(config, logger);
        _processing = new(logger, Name, () => _settings.DebugLogging.Enabled);
        _configPath = config.ConfigFilePath;
        _coverageRequests = new WeatherGuardCoverageRequests(Path.GetDirectoryName(_configPath)!, logger);
        _adminDiagnostics = new WeatherGuardDiagnostics(logger, false);
    }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        DiagnosticLog.Write(_logger, Name, _settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        WeatherGuardDiagnostics.StartupSettings(_logger, _settings);
        if (!_settings.Enabled.Value)
        { return; }
        if (!_compatibility.Supports(gameVersion))
        { DiagnosticLog.Write(_logger, "WeatherGuard", $"DISABLED (unsupported Valheim version {gameVersion}; required version {_compatibility.RequiredVersion}; built-in audited version {GameVersionCompatibility.AuditedVersion}).", level: LogLevel.Warning); return; }
        IsActive = true;
        if (_settings.DebugLogging.Enabled)
            DiagnosticLog.Write(_logger, Name, "Waiting for joined players and dedicated-server world", level: LogLevel.Debug);
        _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail, () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady);
    }
    public void Tick(float now)
    {
        if (IsActive && !_settings.Enabled.Value) Shutdown();
        if (IsActive)
        {
            if (ServerReady) _processing.ServerReady();
            if (_occupied != true) StopController();
            else
            {
                StartController();
                _controller?.Tick(now);
            }
        }
        PollCoverage(now);
    }
    private static bool ServerReady => WorldServices.Terrain;
    private void PresenceChanged(bool? occupied)
    {
        _occupied = occupied;
        if (!IsActive) return;
        if (occupied != true)
        {
            StopController();
            if (occupied.HasValue) _processing.EmptyPaused();
            else _processing.Unavailable("player presence unavailable");
        }
        // Activation runs in Tick after server readiness is announced.
    }
    private void StartController()
    {
        var net = ZNet.instance; var world = ZDOMan.instance;
        if (!WorldServices.Terrain) { StopController(); _processing.Paused("world services unavailable"); return; }
        if (!ReferenceEquals(_world, world) && _controller != null) { StopController(); _processing.Paused("world changed"); }
        if (_controller != null) return;
        _world = world;
        _controller = new WeatherGuardController(this, _logger, _settings.DebugLogging, _settings.DebugLogLevel.Value, _settings.LogWorldCreatedItems.Value, _settings.ProtectRain.Value, _settings.ProtectUnderwater.Value, _settings.ExcludedPrefabs.Value, _players.Presence);
        _controller.Install();
        _processing.Ready();
    }
    private void StopController()
    {
        var controller = _controller; _controller = null; if (controller != null) ModuleScheduler.Current.Forget(controller); _world = null;
        try { controller?.Dispose(); } finally { WorldObjectProvider.Current?.ReleaseOwner(Name); }
    }
    private float _coverageNow;
    private readonly Action _coverageReport;
    private void PollCoverage(float now) { _coverageNow = now; _coverageRequests.Poll(now, _coverageReport); }
    private void ReportCoverage() => _adminDiagnostics.Coverage(
        (_controller != null && ReferenceEquals(_world, ZDOMan.instance) && WorldServices.Terrain ? _controller.Coverage(_coverageNow) : null)
        ?? new CoverageSnapshot { State = !IsActive ? "module_inactive" : _occupied == false ? "server_empty" : _occupied == null ? "player_presence_unavailable" : "waiting_for_world",
            RainEnabled = _settings.ProtectRain.Value, UnderwaterEnabled = _settings.ProtectUnderwater.Value });
    internal void Fail(Exception error)
    {
        DiagnosticLog.Write(_logger, "WeatherGuard", CoreLogTopics.Lifecycle, $"Disabled after controller error; returning pieces to vanilla ownership.", level: LogLevel.Error, error: error);
        Shutdown();
    }
    public void Shutdown()
    {
        IsActive = false; _occupied = null; _processing.Reset();
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController);
    }
}
