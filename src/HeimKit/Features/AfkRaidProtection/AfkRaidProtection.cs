using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Shared;
namespace HeimKit.Features.AfkRaidProtection;
public sealed class AfkRaidProtection : IServerModule
{
    private readonly AfkRaidProtectionSettings _settings;
    private readonly ManualLogSource _logger;
    private AfkRaidProtectionController? _controller;
    public string Name => "AfkRaidProtection";
    public bool IsActive { get; private set; }
    public AfkRaidProtection(ConfigFile config, ManualLogSource logger) { _logger = logger; _settings = new(config, logger); }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        if (!_settings.Enabled.Value) { Log("DISABLED by configuration"); return; }
        if (gameVersion != "1.0.17") { Log("DISABLED: unsupported game version " + gameVersion, LogLevel.Warning); return; }
        IsActive = true; Log("ENABLED; waiting for dedicated-server world");
    }
    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        try
        {
            if (!PlayerActivityService.Ready) { _controller?.Dispose(); _controller = null; return; }
            if (_controller == null) { _controller = new(this, _settings, _logger); Log("Ready; native server hooks installed"); }
            _controller.Tick(now);
        }
        catch (Exception error) { Fail(error); }
    }
    internal void Fail(Exception error) { Log("DISABLED after runtime failure: " + error, LogLevel.Error); Shutdown(); }
    private void Log(string message, LogLevel level = LogLevel.Info) => DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, message, new (string, object?)[] { ("debug_mode", _settings.DebugLogging.Enabled), ("debug_suppressed", _settings.DebugLogging.Suppressed), ("idle_timeout_s", _settings.Activity.IdleTimeoutSeconds.Value), ("join_grace_s", _settings.Activity.JoinGraceSeconds.Value) }, level: level);
    public void Shutdown() { IsActive = false; var controller = _controller; _controller = null; controller?.Dispose(); }
}
