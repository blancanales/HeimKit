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

    public string Name => "Infinite Torches";
    public bool IsActive { get; private set; }

    public InfiniteTorches(ConfigFile config, ManualLogSource logger)
    {
        _logger = logger;
        _settings = new InfiniteTorchesSettings(config, logger);
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        if (!_settings.Enabled.Value)
        {
            DiagnosticLog.Write(_logger, "Infinite Torches", DiagnosticEvent.Lifecycle, "DISABLED (Torches.Enabled = false).");
            return;
        }
        if (gameVersion != "1.0.17")
        {
            DiagnosticLog.Write(_logger, "Infinite Torches", DiagnosticEvent.Lifecycle, $"DISABLED (unsupported Valheim version {gameVersion}; audited for 1.0.17). Configuration requests Enabled = true.", level: LogLevel.Warning);
            return;
        }
        _controller = new InfiniteTorchesController(this, _settings, _logger);
        IsActive = true;
        DiagnosticLog.Write(_logger, "Infinite Torches", DiagnosticEvent.Lifecycle, "ENABLED; waiting for dedicated-server world", new (string, object?)[]
        { ("interval_s", _settings.IntervalSeconds.Value), ("maximum_requests_per_pass", _settings.MaxMessagesPerPass.Value), ("debug_mode", _settings.DebugLogging.Enabled), ("global_debug_mode", _settings.DebugMode.Value), ("debug_suppressed", _settings.DebugLogging.Suppressed) });
    }

    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        _controller?.Tick(now);
    }

    internal void Fail(Exception error, string operation)
    {
        DiagnosticLog.Write(_logger, "Infinite Torches", DiagnosticEvent.Lifecycle, $"DISABLED after runtime error while {operation}: {error}", level: LogLevel.Error);
        Shutdown();
    }

    public void Shutdown()
    {
        IsActive = false;
        var controller = _controller;
        _controller = null;
        controller?.Dispose();
    }
}
