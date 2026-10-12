using HeimKit.Shared.Players;
using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;

namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorches : IServerModule
{
    private readonly InfiniteTorchesSettings _settings;
    private readonly ManualLogSource _logger;
    private InfiniteTorchesController? _controller;
    private readonly PlayerServices _players;
    private IDisposable? _presenceRegistration;
    private bool _occupied;
    private ZDOMan? _world;
    private readonly PlayerProcessingNotices _processing;

    public string Name => "InfiniteTorches";
    public bool IsActive { get; private set; }

    public InfiniteTorches(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _logger = logger;
        _players = players;
        _settings = new InfiniteTorchesSettings(config, logger);
        _processing = new(logger, Name, () => _settings.DebugLogging.Enabled);
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, _settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!_settings.Enabled.Value)
        {
            return;
        }
        if (gameVersion != "1.0.17")
        {
            DiagnosticLog.Write(_logger, "InfiniteTorches", DiagnosticEvent.Lifecycle, $"DISABLED (unsupported Valheim version {gameVersion}; audited for 1.0.17). Configuration requests Enabled = true.", level: LogLevel.Warning);
            return;
        }
        IsActive = true;
        if (_settings.DebugLogging.Enabled)
            DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, "Waiting for joined players and dedicated-server world", new (string, object?)[]
            { ("interval_s", _settings.IntervalSeconds.Value), ("maximum_requests_per_pass", _settings.MaxMessagesPerPass.Value), ("debug_mode", true), ("log_schema", 1) }, LogLevel.Debug);
        _presenceRegistration = _players.Events.Subscribe(PresenceChanged, error => Fail(error, "responding to player presence"), () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady);
    }

    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        if (ServerReady) _processing.ServerReady();
        if (!_occupied) return;
        var net = ZNet.instance; var world = ZDOMan.instance;
        if (net == null || world == null || ZNetScene.instance == null || ZRoutedRpc.instance == null || !net.IsServer() || !net.IsDedicated())
        { StopController(); _processing.Paused("world services unavailable"); return; }
        if (_world != null && !ReferenceEquals(_world, world)) { StopController(); _processing.Paused("world changed"); }
        _world = world;
        if (_controller == null) _controller = new(this, _settings, _logger, _players.Presence);
        _controller.Tick(now);
    }

    private static bool ServerReady => ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated()
        && ZDOMan.instance != null && ZNetScene.instance != null && ZRoutedRpc.instance != null;
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
        if (_settings.Enabled.Value && _controller == null) _controller = new(this, _settings, _logger, _players.Presence);
    }
    internal void ProcessingReady() => _processing.Ready();
    private void StopController()
    {
        var controller = _controller; _controller = null; _world = null;
        controller?.Dispose();
    }

    internal void Fail(Exception error, string operation)
    {
        DiagnosticLog.Write(_logger, "InfiniteTorches", DiagnosticEvent.Lifecycle, $"DISABLED after runtime error while {operation}: {error}", level: LogLevel.Error);
        Shutdown();
    }

    public void Shutdown()
    {
        IsActive = false; _occupied = false; _processing.Reset();
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController);
    }
}
