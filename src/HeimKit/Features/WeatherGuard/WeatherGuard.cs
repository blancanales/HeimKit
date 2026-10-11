using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;

namespace HeimKit.Features.WeatherGuard;

public sealed class WeatherGuard : IServerModule
{
    private readonly WeatherGuardSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly string _configPath;
    private WeatherGuardController? _controller;
    private ZDOMan? _world;
    public string Name => "WeatherGuard";
    public bool IsActive { get; private set; }

    public WeatherGuard(ConfigFile config, ManualLogSource logger)
    {
        _logger = logger;
        _settings = new WeatherGuardSettings(config);
        _configPath = config.ConfigFilePath;
    }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        if (!_settings.Enabled.Value)
        { _logger.LogInfo("WeatherGuard: DISABLED (WeatherGuard.Enabled = false)."); return; }
        if (gameVersion != "1.0.17")
        { _logger.LogWarning($"WeatherGuard: DISABLED (unsupported Valheim version {gameVersion}; audited for 1.0.17)."); return; }
        IsActive = true;
        _logger.LogInfo($"WeatherGuard: ENABLED; Valheim={gameVersion}; config={_configPath}; DebugMode={_settings.DebugMode.Value}. Waiting for dedicated-server world.");
        _logger.LogInfo("WeatherGuard: player-built structures use server processing when surrounding terrain and objects are ready. Existing creators are preserved. No logout or repair required. World-created claims are inactive. Live rain/combat/support validation pending.");
    }
    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        var net = ZNet.instance; var world = ZDOMan.instance;
        if (net == null || world == null || ZNetScene.instance == null || ZoneSystem.instance == null || ZRoutedRpc.instance == null ||
            !net.IsServer() || !net.IsDedicated()) return;
        if (!ReferenceEquals(_world, world))
        { _controller?.Dispose(); _controller = null; _world = world; }
        if (_controller == null)
        {
            _controller = new WeatherGuardController(this, _logger, _settings.DebugMode.Value);
            _controller.Install();
            _logger.LogInfo("WeatherGuard: ready. Server processing and client-update guards installed; awaiting eligible player-built structures. Rain/water wear is suppressed only during protected server wear passes.");
        }
        _controller.Tick(now);
    }
    internal void Fail(Exception error)
    {
        _logger.LogError($"WeatherGuard: DISABLED after controller error; returning pieces to vanilla ownership: {error}");
        Shutdown();
    }
    public void Shutdown()
    {
        IsActive = false;
        var controller = _controller; _controller = null; _world = null;
        controller?.Dispose();
    }
}
