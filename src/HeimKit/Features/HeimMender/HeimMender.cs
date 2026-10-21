using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Shared;
using HeimKit.Shared.Players;
namespace HeimKit.Features.HeimMender;
public sealed class HeimMender : IServerModule
{
    private readonly ConfigFile _config;
    private readonly HeimMenderSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
    private readonly PlayerServices _players;
    private readonly PlayerProcessingNotices _processing;
    private HeimMenderController? _controller;
    private HeimMenderConfiguration? _configuration;
    private HeimMenderAdministration? _administration;
    private IDisposable? _presenceRegistration;
    private bool _occupied, _compatible, _failed;
    private int _requestId;
    private ZDOMan? _world;
    private long _worldUid;
    public string Name => "HeimMender";
    public bool IsActive { get; private set; }
    public HeimMender(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _config = config; _logger = logger; _players = players;
        _compatibility = GameVersionCompatibility.Resolve(config, logger); _settings = new(config, logger);
        _processing = new(logger, Name, () => _settings.DebugLogging.Enabled);
    }
    public void Initialize(string gameVersion)
    {
        Shutdown(); _failed = false; _compatible = _compatibility.Supports(gameVersion);
        _requestId = _settings.RepairRequestId.Value;
        Log(_settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!_compatible) { if (_settings.Enabled.Value) Log("DISABLED: unsupported game version " + gameVersion + "; required version " + _compatibility.RequiredVersion, LogLevel.Warning); return; }
        _configuration = new(_config, _settings, _logger);
        ApplyEnabled();
    }
    private void ApplyEnabled()
    {
        bool enabled = _settings.Enabled.Value && _compatible && !_failed;
        if (enabled == IsActive) return;
        if (!enabled)
        {
            IsActive = false; _occupied = false;
            StopController(); var registration = _presenceRegistration; _presenceRegistration = null; registration?.Dispose();
            _processing.Paused("disabled by configuration");
            Log("Disabled."); return;
        }
        IsActive = true;
        try { _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail, () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady); }
        catch (Exception error) { Fail(error); }
        if (!IsActive) { var registration = _presenceRegistration; _presenceRegistration = null; registration?.Dispose(); }
    }
    public void Tick(float now)
    {
        _configuration?.Tick(now);
        ApplyEnabled();
        try
        {
            bool ready = PlayerServices.Ready && ZDOMan.instance != null && ZNetScene.instance != null && ZoneSystem.instance != null && ZRoutedRpc.instance != null;
            if (_compatible && !_failed && ready && _administration == null) _administration = new(Request, () => _controller?.Status ?? "HeimMender: no active repair scope.");
            if (IsActive && ready) _processing.ServerReady();
            if (!IsActive || !_occupied || !ready)
            {
                StopController();
                if (IsActive && _occupied && !ready) _processing.Paused("world services unavailable");
            }
            else
            {
                long uid = ZNet.instance.GetWorldUID();
                if (!ReferenceEquals(_world, ZDOMan.instance) || _worldUid != uid)
                { StopController(); _world = ZDOMan.instance; _worldUid = uid; }
                if (_controller == null) { _controller = new(_settings, _logger, _players, Fail); _processing.Ready(); }
                _controller.Tick(now);
            }
            int request = _settings.RepairRequestId.Value;
            if (request > _requestId)
            {
                _requestId = request;
                Request(_settings.RepairTarget.Value, null);
            }
        }
        catch (Exception error) { Fail(error); }
    }
    private void Request(string target, float? radius)
    {
        try
        {
            if (!IsActive || _controller == null) { Log("Repair request rejected: enabled gameplay and a ready joined player are required.", LogLevel.Warning); return; }
            _controller.Request(target.Trim(), radius, UnityEngine.Time.unscaledTime);
        }
        catch (Exception error) { Fail(error); }
    }
    private void PresenceChanged(bool? occupied)
    {
        _occupied = occupied == true;
        if (!IsActive || _occupied) return;
        StopController();
        if (occupied.HasValue) _processing.EmptyPaused(); else _processing.Unavailable("player presence unavailable");
    }
    private void StopController() { var controller = _controller; _controller = null; controller?.Dispose(); }
    internal void Fail(Exception error)
    {
        if (_failed) return;
        _failed = true; Log("DISABLED after runtime failure: " + error, LogLevel.Error);
        IsActive = false; _occupied = false;
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController, () => _administration?.Dispose());
        _administration = null;
    }
    private void Log(string message, LogLevel level = LogLevel.Info) => DiagnosticLog.Write(_logger, Name, DiagnosticEvent.Lifecycle, message, level: level);
    public void Shutdown()
    {
        IsActive = false; _compatible = false; _occupied = false; _processing.Reset();
        var registration = _presenceRegistration; _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController, () => _configuration?.Dispose(), () => _administration?.Dispose());
        _configuration = null; _administration = null; _world = null;
    }
}
