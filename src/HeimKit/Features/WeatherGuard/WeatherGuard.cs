using System;
using System.IO;
using HeimKit.Features.WeatherGuard.Models;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;

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
    public string Name => "WeatherGuard";
    public bool IsActive { get; private set; }

    public WeatherGuard(ConfigFile config, ManualLogSource logger)
    {
        _logger = logger;
        _settings = new WeatherGuardSettings(config, logger);
        _configPath = config.ConfigFilePath;
        _coverageRequests = new WeatherGuardCoverageRequests(Path.GetDirectoryName(_configPath)!, logger);
        _adminDiagnostics = new WeatherGuardDiagnostics(logger, false);
    }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        if (!_settings.Enabled.Value)
        { DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, "DISABLED (WeatherGuard.Enabled = false)."); return; }
        if (gameVersion != "1.0.17")
        { DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, $"DISABLED (unsupported Valheim version {gameVersion}; audited for 1.0.17).", level: LogLevel.Warning); return; }
        IsActive = true;
        DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, "ENABLED; waiting for dedicated-server world", new (string, object?)[] { ("game_version", gameVersion), ("config", _configPath), ("debug_mode", _settings.DebugLogging.Enabled), ("global_debug_mode", _settings.DebugMode.Value), ("debug_suppressed", _settings.DebugLogging.Suppressed), ("rain_protection", _settings.ProtectRain.Value), ("underwater_protection", _settings.ProtectUnderwater.Value), ("debug_log_level", _settings.DebugLogLevel.Value), ("log_world_created_items", _settings.LogWorldCreatedItems.Value) });
        DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, _settings.ProtectRain.Value || _settings.ProtectUnderwater.Value
            ? "player-built structures use server processing when surrounding terrain and objects are ready. Existing creators are preserved. No logout or repair required. World-created claims are inactive."
            : "Rain and underwater protections are both disabled; no pieces are acquired. Coverage reports remain available.");
    }
    public void Tick(float now)
    {
        if (!IsActive) { PollCoverage(now); return; }
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        var net = ZNet.instance; var world = ZDOMan.instance;
        if (net == null || world == null || ZNetScene.instance == null || ZoneSystem.instance == null || ZRoutedRpc.instance == null ||
            !net.IsServer() || !net.IsDedicated()) { PollCoverage(now); return; }
        if (!ReferenceEquals(_world, world))
        { _controller?.Dispose(); _controller = null; _world = world; }
        if (_controller == null)
        {
            _controller = new WeatherGuardController(this, _logger, _settings.DebugLogging.Enabled, _settings.DebugLogLevel.Value, _settings.LogWorldCreatedItems.Value, _settings.ProtectRain.Value, _settings.ProtectUnderwater.Value, _settings.ExcludedPrefabs.Value);
            _controller.Install();
            DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, "ready. Server processing, weather-damage gate and client-update guards installed; awaiting eligible player-built structures. Native wetness, support and commands retained.");
        }
        _controller.Tick(now);
        PollCoverage(now);
    }
    private void PollCoverage(float now) => _coverageRequests.Poll(now, () =>
        _adminDiagnostics.Coverage((_controller != null && ReferenceEquals(_world, ZDOMan.instance) && ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated() && ZNetScene.instance != null && ZoneSystem.instance != null ? _controller.Coverage(now) : null) ?? new CoverageSnapshot
        { State = IsActive ? "waiting_for_world" : "module_inactive", RainEnabled = _settings.ProtectRain.Value, UnderwaterEnabled = _settings.ProtectUnderwater.Value }));
    internal void Fail(Exception error)
    {
        DiagnosticLog.Write(_logger, "WeatherGuard", DiagnosticEvent.Lifecycle, $"DISABLED after controller error; returning pieces to vanilla ownership: {error}", level: LogLevel.Error);
        Shutdown();
    }
    public void Shutdown()
    {
        IsActive = false;
        var controller = _controller; _controller = null; _world = null;
        controller?.Dispose();
    }
}
