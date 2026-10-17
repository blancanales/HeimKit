using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
using HeimKit.Shared.Players;

namespace HeimKit.Features.SharedMap;

public sealed class SharedMap : IServerModule
{
    private readonly SharedMapSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
    private readonly PlayerServices _players;
    private readonly PlayerProcessingNotices _processing;
    private IDisposable? _presenceRegistration;
    private bool _occupied;

    public string Name => "SharedMap";
    public bool IsActive { get; private set; }

    public SharedMap(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _settings = new SharedMapSettings(config, logger);
        _logger = logger;
        _compatibility = GameVersionCompatibility.Resolve(config, logger);
        _players = players;
        _processing = new PlayerProcessingNotices(logger, Name, () => _settings.DebugLogging.Enabled);
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        Log(_settings.Enabled.Value ? "Enabled. Mapping behavior is not implemented." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!_settings.Enabled.Value) return;
        if (!_compatibility.Supports(gameVersion))
        {
            Log("DISABLED: unsupported game version " + gameVersion + "; required version " + _compatibility.RequiredVersion, LogLevel.Warning);
            return;
        }

        IsActive = true;
        try
        {
            _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail,
                () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady);
        }
        catch (Exception error) { Fail(error); }
    }

    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        try
        {
            bool ready = PlayerServices.Ready;
            if (ready) _processing.ServerReady();
            if (!_occupied) return;
            if (!ready) { _processing.Paused("world services unavailable"); return; }
            // Only lifecycle acknowledgement: no controller or mapping operations exist yet.
            _processing.Ready();
        }
        catch (Exception error) { Fail(error); }
    }

    private void PresenceChanged(bool? occupied)
    {
        _occupied = occupied == true;
        if (!IsActive || _occupied) return;
        // There are no gameplay resources to release in the blank module.
        if (occupied.HasValue) _processing.EmptyPaused();
        else _processing.Unavailable("player presence unavailable");
    }

    private void Fail(Exception error)
    {
        if (!IsActive) return;
        Log("DISABLED after runtime failure: " + error, LogLevel.Error);
        Shutdown();
    }

    private void Log(string message, LogLevel level = LogLevel.Info) =>
        DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, message, level: level);

    public void Shutdown()
    {
        IsActive = false;
        _occupied = false;
        _processing.Reset();
        var registration = _presenceRegistration;
        _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose());
    }
}
