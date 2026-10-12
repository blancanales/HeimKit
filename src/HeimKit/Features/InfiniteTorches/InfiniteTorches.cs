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

    public string Name => "Infinite Torches";
    public bool IsActive { get; private set; }

    public InfiniteTorches(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _logger = logger;
        _players = players;
        _settings = new InfiniteTorchesSettings(config, logger);
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, _settings.Enabled.Value ? "Enabled." : "Disabled.");
        if (!_settings.Enabled.Value)
        {
            return;
        }
        if (gameVersion != "1.0.17")
        {
            DiagnosticLog.Write(_logger, "Infinite Torches", DiagnosticEvent.Lifecycle, $"DISABLED (unsupported Valheim version {gameVersion}; audited for 1.0.17). Configuration requests Enabled = true.", level: LogLevel.Warning);
            return;
        }
        IsActive = true;
        if (_settings.DebugLogging.Enabled)
            DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, "Waiting for connected players and dedicated-server world", new (string, object?)[]
            { ("interval_s", _settings.IntervalSeconds.Value), ("maximum_requests_per_pass", _settings.MaxMessagesPerPass.Value), ("debug_mode", true), ("log_schema", 1) }, LogLevel.Debug);
        _presenceRegistration = _players.Events.Subscribe(PresenceChanged, error => Fail(error, "responding to player presence"));
    }

    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        if (!_occupied) return;
        _controller?.Tick(now);
    }

    private void PresenceChanged(bool? occupied)
    {
        _occupied = occupied == true;
        if (!IsActive) return;
        if (!_occupied) { StopController(); return; }
        if (_settings.Enabled.Value && _controller == null) _controller = new(this, _settings, _logger, _players.Presence);
    }
    private void StopController()
    {
        var controller = _controller; _controller = null;
        controller?.Dispose();
    }

    internal void Fail(Exception error, string operation)
    {
        DiagnosticLog.Write(_logger, "Infinite Torches", DiagnosticEvent.Lifecycle, $"DISABLED after runtime error while {operation}: {error}", level: LogLevel.Error);
        Shutdown();
    }

    public void Shutdown()
    {
        IsActive = false; _occupied = false;
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController);
    }
}
