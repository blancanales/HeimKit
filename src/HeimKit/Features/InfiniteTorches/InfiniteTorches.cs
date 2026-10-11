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
        _settings = new InfiniteTorchesSettings(config);
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        if (!_settings.Enabled.Value)
        {
            _logger.LogInfo("Infinite Torches: DISABLED (Torches.Enabled = false).");
            return;
        }
        if (gameVersion != "1.0.17")
        {
            _logger.LogWarning($"Infinite Torches: DISABLED (unsupported Valheim version {gameVersion}; audited for 1.0.17). Configuration requests Enabled = true.");
            return;
        }
        _controller = new InfiniteTorchesController(this, _settings, _logger);
        IsActive = true;
        _logger.LogInfo($"Infinite Torches: ENABLED (Torches.Enabled = true; interval {_settings.IntervalSeconds.Value}s; maximum {_settings.MaxMessagesPerPass.Value} refill requests per pass; {_controller.SettingsDescription}). Waiting for the dedicated-server world.");
    }

    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        _controller?.Tick(now);
    }

    internal void Fail(Exception error, string operation)
    {
        _logger.LogError($"Infinite Torches: DISABLED after runtime error while {operation}: {error}");
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
