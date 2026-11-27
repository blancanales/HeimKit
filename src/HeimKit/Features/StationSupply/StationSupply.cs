using System.Collections.Generic;
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
namespace HeimKit.Features.StationSupply;
public sealed class StationSupply : IServerModule, IModuleSwitches, IModuleNumericSettings, IModuleIdle
{
    private readonly StationSupplySettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
    private readonly PlayerServices _players;
    private StationSupplyController? _controller;
    private InitialStaticRevision? _initialRevision;
    private IDisposable? _presenceRegistration;
    private bool _occupied;
    private ZDOMan? _world;
    private long _worldUid;
    private readonly PlayerProcessingNotices _processing;
    bool IModuleIdle.Idle => _controller?.Idle != false;
    void IModuleIdle.HoldNewWork(bool hold)
    {
        if (_controller != null) _controller.HoldNewWork = hold;
    }

    private SwitchBinding[]? _switches;
    IReadOnlyList<SwitchBinding> IModuleSwitches.Switches => _switches ??= new SwitchBinding[]
    {
        new("Enabled", _settings.Enabled),
        new("Debug logging", _settings.DebugMode, applyLive: true),
        new("Smelters", _settings.Smelters),
        new("Charcoal kilns", _settings.CharcoalKilns),
        new("Blast furnaces", _settings.BlastFurnaces),
        new("Windmills", _settings.Windmills),
        new("Spinning wheels", _settings.SpinningWheels),
        new("Eitr refineries", _settings.EitrRefineries),
        new("Frigid Kilns", _settings.FrigidKilns),
        new("Shield generators", _settings.ShieldGenerators),
        new("Fermenters", _settings.Fermenters),
        new("Obliterators", _settings.Obliterators),
    };
    SwitchApplyMode IModuleSwitches.ApplyMode => SwitchApplyMode.Reinitialize;

    IReadOnlyList<NumericSettingBinding> IModuleNumericSettings.NumericSettings => new[]
    {
        NumericSettingBinding.Create("Items per visit", _settings.MaxItemsPerVisit,
            "Maximum items per intake visit, limited by station capacity. Fermenters take one item.",
            () => "Applies when the next batch is sized.")
    };

    public string Name => "StationSupply";
    public bool IsActive { get; private set; }
    public StationSupply(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _logger = logger;
        _compatibility = GameVersionCompatibility.Resolve(config, logger);
        _settings = new(config, logger);
        _players = players;
        _processing = new(logger, Name, () => _settings.DebugLogging.Enabled);
    }
    public void Initialize(string gameVersion)
    {
        Shutdown();
        Log(_settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!ModuleFailureProtection.MayStart(Name)) return;
        if (!_settings.Enabled.Value) return;
        if (!_compatibility.Supports(gameVersion)) { Log("DISABLED: unsupported game version " + gameVersion + "; required version " + _compatibility.RequiredVersion, LogLevel.Warning); return; }
        IsActive = true;
        if (_settings.DebugLogging.Enabled) Log("Waiting for joined players and dedicated-server world", LogLevel.Debug);
        try
        {
            _initialRevision = new(StationSupplyPrefabs.FirstHandoffEligibility(_settings));
            _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail, () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady);
        }
        catch (Exception error) { Fail(error); }
        if (!IsActive) { var registration = _presenceRegistration; _presenceRegistration = null; registration?.Dispose(); }
    }
    public void Tick(float now)
    {
        if (!IsActive) return;
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        try
        {
            bool ready = WorldServices.Activity && WorldServices.Terrain && EnvMan.instance != null;
            if (ready) _processing.ServerReady();
            if (!_occupied) { StopController(); return; }
            if (!ready) { StopController(); _processing.Paused("world services unavailable"); return; }
            long uid = ZNet.instance.GetWorldUID();
            if (!ReferenceEquals(_world, ZDOMan.instance) || _worldUid != uid)
            { StopController(); _world = ZDOMan.instance; _worldUid = uid; }
            StartController();
            _controller?.Tick(now);
        }
        catch (Exception error) { Fail(error); }
    }
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
        // Activation runs in Tick after server readiness is announced.
    }
    private void StartController()
    {
        if (!_settings.Enabled.Value || _controller != null) return;
        _controller = new(this, _settings, _logger, _players);
        _processing.Ready();
    }
    private void StopController()
    {
        var controller = _controller; _controller = null; if (controller != null) ModuleScheduler.Current.Forget(controller);
        try { controller?.Dispose(); } finally { WorldObjectProvider.Current?.ReleaseOwner(Name); }
    }
    internal void Fail(Exception error) { if (!IsActive) return; ModuleFailureProtection.Record(Name, error, "controller/native callback or runtime processing"); DiagnosticLog.Write(_logger, Name, CoreLogTopics.Lifecycle, "Disabled after a runtime failure.", _controller?.FailureContext() ?? default, level: LogLevel.Error, error: error); Shutdown(); }
    private void Log(string message, LogLevel level = LogLevel.Info) => DiagnosticLog.Write(_logger, Name, message, level: level);
    public void Shutdown()
    {
        IsActive = false; _occupied = false; _processing.Reset();
        var registration = _presenceRegistration; _presenceRegistration = null;
        var initialRevision = _initialRevision; _initialRevision = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController, () => initialRevision?.Dispose());
    }
}
