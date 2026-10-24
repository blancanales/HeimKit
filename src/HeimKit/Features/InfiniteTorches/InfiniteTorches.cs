using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Configuration;
using HeimKit.Core.Players;
using System;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HeimKit.Features.InfiniteTorches;

public sealed class InfiniteTorches : IServerModule
{
    private readonly InfiniteTorchesSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
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
        _logger = logger; _compatibility = GameVersionCompatibility.Resolve(config, logger);
        _players = players;
        _settings = new InfiniteTorchesSettings(config, logger);
        _processing = new(logger, Name, () => _settings.DebugLogging.Enabled);
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        DiagnosticLog.Write(_logger, Name, _settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!_settings.Enabled.Value)
        {
            return;
        }
        if (!_compatibility.Supports(gameVersion))
        {
            DiagnosticLog.Write(_logger, "InfiniteTorches", $"DISABLED (unsupported Valheim version {gameVersion}; required version {_compatibility.RequiredVersion}; built-in audited version {GameVersionCompatibility.AuditedVersion}). Configuration requests Enabled = true.", level: LogLevel.Warning);
            return;
        }
        IsActive = true;
        if (_settings.DebugLogging.Enabled)
            if (DiagnosticLog.DebugAvailable(_logger, Name)) DiagnosticLog.Write(_logger, Name, CoreLogTopics.Lifecycle, "Waiting for joined players and dedicated-server world", new (string, object?)[] { ("interval_s", _settings.IntervalSeconds.Value), ("maximum_requests_per", _settings.MaxMessagesPerPass.Value), ("debug_mode", true), ("log_schema", 1) }, LogLevel.Debug);
        _presenceRegistration = _players.Events.Subscribe(PresenceChanged, error => Fail(error, "responding to player presence"), () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady);
    }

    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        if (ServerReady) _processing.ServerReady();
        if (!_occupied) return;
        var net = ZNet.instance; var world = ZDOMan.instance;
        if (!WorldObjectProvider.Current.Ready)
        { StopController(); _processing.Paused("world services unavailable"); return; }
        if (_world != null && !ReferenceEquals(_world, world)) { StopController(); _processing.Paused("world changed"); }
        _world = world;
        if (_controller == null) _controller = new(this, _settings, _logger, _players.Presence);
        _controller.Tick(now);
    }

    private static bool ServerReady => WorldServices.World;
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
        var controller = _controller; _controller = null; if (controller != null) ModuleScheduler.Current.Forget(controller); _world = null;
        try { controller?.Dispose(); } finally { WorldObjectProvider.Current?.ReleaseOwner(Name); }
    }

    internal void Fail(Exception error, string operation)
    {
        DiagnosticLog.Write(_logger, "InfiniteTorches", CoreLogTopics.Lifecycle, $"Disabled after runtime error while {operation}.", level: LogLevel.Error, error: error);
        Shutdown();
    }

    public void Shutdown()
    {
        IsActive = false; _occupied = false; _processing.Reset();
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController);
    }
}
