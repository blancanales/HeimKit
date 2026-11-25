using System.Collections.Generic;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Players;

namespace HeimKit.Features.SharedMap;

public sealed class SharedMap : IServerModule, IModuleSwitches, IModuleNumericSettings, IModuleIdle
{
    private readonly SharedMapSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
    private readonly PlayerServices _players;
    private readonly PlayerProcessingNotices _processing;
    private IDisposable? _presenceRegistration;
    private bool _occupied;
    private SharedMapController? _controller;
    private Exception? _callbackFailure;
    private SharedMapExploration? _exploration;
    private ZDOMan? _world;
    private long _worldUid;
    private readonly string _storeDirectory;
    private readonly SharedMapDiagnostics _diagnostics;
    private readonly ProcessingTiming _timing;
    private readonly Func<int> _timingScope;
    private SharedMapStore? _store;
    private SharedMapBootstrap? _bootstrap;
    private SharedMapMarkerRefresh? _portalRefresh;
    private SharedMapResourceStore? _resources;
    private SharedMapMarkerRefresh? _resourceRefresh;
    private SharedMapDungeonStore? _dungeons;
    private SharedMapPinState? _pinState;
    private SharedMapLandmarkStore? _runestones, _spawners, _structures;

    internal bool PlayerHosted => _players.PlayerHosted;
    internal bool CallbackFailed => _callbackFailure != null;
    internal string StoreDirectory => _storeDirectory;
    private float _recoverUntil;
    private bool _holdNewWork;
    bool IModuleIdle.Idle => _controller?.Idle != false && _bootstrap == null &&
        _store?.Clean != false && _portalRefresh?.Clean != false && _resources?.Clean != false &&
        _resourceRefresh?.Clean != false && _dungeons?.Clean != false && _pinState?.Clean != false &&
        _runestones?.Clean != false && _spawners?.Clean != false && _structures?.Clean != false;

    void IModuleIdle.HoldNewWork(bool hold)
    {
        _holdNewWork = hold;
        if (hold) _holdSaveWarned = false;
        if (_controller != null) _controller.HoldNewWork = hold;
    }

    private SwitchBinding[]? _switches;
    IReadOnlyList<SwitchBinding> IModuleSwitches.Switches => _switches ??= new SwitchBinding[]
    {
        new("Enabled", _settings.Enabled),
        new("Debug logging", _settings.DebugMode, applyLive: true),
        new("Portal markers", _settings.PortalMarkerSync, applyInPlace: () => ApplySwitchInPlace(refreshPortals: _settings.PortalMarkerSync.Value)),
        new("Resource markers", _settings.ResourceMarkerSync),
        new("Discover resources in explored areas", _settings.ResourceDiscoverPreviouslyExplored, applyInPlace: ApplySwitchInPlace),
        new("Remove depleted resources", _settings.ResourceRemoveDepleted, applyInPlace: ApplySwitchInPlace),
        new("Dungeon markers", _settings.DungeonMarkerSync),
        new("Discover dungeons in explored areas", _settings.DungeonDiscoverPreviouslyExplored, applyInPlace: ApplySwitchInPlace),
        new("Allow player map writes", _settings.AllowPlayerMapWrites, applyInPlace: ApplySwitchInPlace),
        new("Share pin checkmarks", _settings.SharePinCheckmarks, applyInPlace: ApplySwitchInPlace),
        new("Dungeon loot progress", _settings.DungeonLootProgress),
        new("Runestone markers", _settings.RunestoneMarkerSync),
        new("Spawner markers", _settings.SpawnerMarkerSync),
        new("Dvergr guard tower markers", _settings.DvergrGuardTowerMarkerSync),
        new("Dvergr lighthouse markers", _settings.DvergrLighthouseMarkerSync),
        new("Dvergr excavation markers", _settings.DvergrExcavationMarkerSync),
        new("Fuling village markers", _settings.FulingVillageMarkerSync),
        new("Other structure markers", _settings.OtherStructureMarkerSync),
    };
    SwitchApplyMode IModuleSwitches.ApplyMode => SwitchApplyMode.Reinitialize;

    IReadOnlyList<NumericSettingBinding> IModuleNumericSettings.NumericSettings => new[]
    {
        NumericSettingBinding.Create("Portal grouping distance (metres)", _settings.PortalClusterDistanceMeters,
            "Nearby portals share a marker. Zero shows individual portal markers. Players may need to read the map table again.",
            () => "Applies during map table reconciliation.")
    };

    public string Name => "SharedMap";
    public bool IsActive { get; private set; }

    public SharedMap(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _settings = new SharedMapSettings(config, logger);
        _logger = logger;
        _compatibility = GameVersionCompatibility.Resolve(config, logger);
        _players = players;
        _storeDirectory = players.PlayerHosted
            ? Path.Combine(Path.GetDirectoryName(config.ConfigFilePath)!, "SharedMap")
            : Path.Combine(Path.GetDirectoryName(config.ConfigFilePath)!, "HeimKit", "SharedMap");
        _diagnostics = new SharedMapDiagnostics(logger, _settings.DebugLogging);
        _timing = new ProcessingTiming(logger, Name, _settings.DebugLogging);
        _timing.RegisterStages("world_state", "initialization", "save_snapshots", "sampling", "resources", "dungeons", "dungeon_loot", "landmarks", "tables");
        _timingScope = () => _players.Presence.JoinedCount ?? 0;
        _processing = new PlayerProcessingNotices(logger, Name, () => _settings.DebugLogging.Enabled);
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        Log(_settings.Enabled.Value ? "Enabled." : "Disabled.");
        _settings.DebugLogging.ReportStartup(_logger, Name, _settings.Enabled.Value);
        if (!ModuleFailureProtection.MayStart(Name)) return;
        if (!_settings.Enabled.Value) return;
        if (!_compatibility.Supports(gameVersion))
        {
            Log("DISABLED: unsupported game version " + gameVersion + "; required version " + _compatibility.RequiredVersion, LogLevel.Warning);
            return;
        }

        IsActive = true;
        ModuleScheduler.Current.Battle.Register(this);
        try
        {
            SharedMapPatches.CheckCompatibility(PlayerHosted);
            if (_settings.ResourceMarkerSync.Value) SharedMapResources.CheckCompatibility();
            if (_settings.DungeonMarkerSync.Value) SharedMapDungeons.CheckCompatibility();
            if (_settings.RunestoneMarkerSync.Value || _settings.SpawnerMarkerSync.Value || _settings.StructureMarkersEnabled) SharedMapLandmarks.CheckCompatibility();
            _presenceRegistration = _players.Events.Subscribe(PresenceChanged, Fail,
                () => _processing.IsEmptyPaused, () => _processing.IsRunning, () => _processing.IsServerReady);
        }
        catch (Exception error) { Fail(error); }
    }

    public void Tick(float now)
    {
        if (!IsActive) return;
        if (_callbackFailure is { } failure) { Fail(failure); return; }
        if (!_settings.Enabled.Value) { Shutdown(); return; }
        using var timing = _settings.DebugLogging.Enabled ? _timing.MeasureValue(now, _timingScope) : default;
        try
        {
            SaveForProcessing(now);
            bool ready = PlayerServices.Ready && ZoneSystem.instance != null && ZRoutedRpc.instance != null && ZNetScene.instance != null && Minimap.instance != null;
            if (ready) _processing.ServerReady();
            if (!ready) { StopController(); if (_occupied) _processing.Paused("world or minimap services unavailable"); return; }
            var minimap = Minimap.instance;
            if (minimap == null) { StopController(); return; }
            long uid = ZNet.instance.GetWorldUID();
            if (!ReferenceEquals(_world, ZDOMan.instance) || _worldUid != uid || _exploration == null ||
                !_exploration.Matches(minimap.m_textureSize, minimap.m_pixelSize, minimap.m_exploreRadius, minimap.m_exploreInterval))
            {
                using var worldTiming = _timing.MeasureStage("world_state");
                StopController();
                FlushStore();
                FlushPortalState();
                FlushResourceState();
                FlushDungeonState();
                var exploration = new SharedMapExploration(minimap.m_textureSize, minimap.m_pixelSize, minimap.m_exploreRadius, minimap.m_exploreInterval);
                var store = new SharedMapStore(_storeDirectory, uid, exploration);
                bool loaded = store.Load();
                var portalRefresh = new SharedMapMarkerRefresh(_storeDirectory, uid, _diagnostics.PortalPublished, _diagnostics.PortalRemoved);
                bool loadedPortalState = portalRefresh.Load();
                SharedMapResourceStore? resources = null;
                SharedMapMarkerRefresh? resourceRefresh = null;
                if (_settings.ResourceMarkerSync.Value)
                {
                    resources = new SharedMapResourceStore(_storeDirectory, uid);
                    bool loadedResources = resources.Load();
                    resourceRefresh = new SharedMapMarkerRefresh(_storeDirectory, uid,
                        _diagnostics.ResourcePublished, resourcePins: true);
                    bool loadedResourcePins = resourceRefresh.Load();
                    _diagnostics.ResourceStateOpened(loadedResources, loadedResourcePins, resources.Deposits.Count);
                }
                _resources = resources; _resourceRefresh = resourceRefresh;
                SharedMapDungeonStore? dungeons = null;
                if (_settings.DungeonMarkerSync.Value)
                {
                    dungeons = new SharedMapDungeonStore(_storeDirectory, uid);
                    bool loadedDungeons = dungeons.Load();
                    _diagnostics.DungeonStateOpened(loadedDungeons, dungeons.Count);
                }
                _dungeons = dungeons;
                _runestones = _settings.RunestoneMarkerSync.Value ? new SharedMapLandmarkStore(_storeDirectory, uid, false) : null;
                _spawners = _settings.SpawnerMarkerSync.Value ? new SharedMapLandmarkStore(_storeDirectory, uid, true) : null;
                _structures = _settings.StructureMarkersEnabled
                    ? new SharedMapLandmarkStore(_storeDirectory, uid, ".structures", 2, _settings.StructurePublicationFilter()) : null;
                _runestones?.Load(); _spawners?.Load(); _structures?.Load();
                _pinState = new SharedMapPinState(_storeDirectory, uid, _diagnostics.PinStateChanged);
                _pinState.Load();
                _exploration = exploration; _store = store;
                _portalRefresh = portalRefresh;
                _bootstrap = loaded ? null : new SharedMapBootstrap(ZDOMan.instance, exploration);
                if (_store != null) _store.Preparation = new(_logger, _settings.DebugLogging, "snapshot_exploration");
                if (_portalRefresh != null) _portalRefresh.Preparation = new(_logger, _settings.DebugLogging, "snapshot_portals");
                if (_resources != null) _resources.Preparation = new(_logger, _settings.DebugLogging, "snapshot_resources");
                if (_resourceRefresh != null) _resourceRefresh.Preparation = new(_logger, _settings.DebugLogging, "snapshot_resource_pins");
                if (_dungeons != null) _dungeons.Preparation = new(_logger, _settings.DebugLogging, "snapshot_dungeons");
                if (_pinState != null) _pinState.Preparation = new(_logger, _settings.DebugLogging, "snapshot_pins");
                if (_runestones != null) _runestones.Preparation = new(_logger, _settings.DebugLogging, "snapshot_runestones");
                if (_spawners != null) _spawners.Preparation = new(_logger, _settings.DebugLogging, "snapshot_spawners");
                if (_structures != null) _structures.Preparation = new(_logger, _settings.DebugLogging, "snapshot_structures");
                _world = ZDOMan.instance; _worldUid = uid;
                _diagnostics.StoreOpened(loaded, uid, store.FilePath, exploration.ExploredPixels);
                _diagnostics.PortalStateOpened(loadedPortalState, portalRefresh.FilePath, portalRefresh.Count);
            }
            if (ModuleScheduler.Current.Battle.Active || ModuleScheduler.Current.Combat.Active(now))
            {
                _recoverUntil = now + 5f;
                _controller?.PauseBackground();
                return;
            }
            if (_bootstrap != null)
            {
                if (!_bootstrap.Tick(now < _recoverUntil ? 1 : _settings.MaxTablesPerPass.Value)) return;
                // Commit even an empty import. Until this succeeds, lifecycle flushes must not save partial work.
                _store!.Save(initialize: true);
                var completed = _bootstrap;
                _bootstrap = null;
                _diagnostics.Bootstrapped(completed.Tables, completed.Imported, completed.Invalid, _exploration!.ExploredPixels);
            }
            if (!_occupied) return;
            if (_controller == null)
            {
                using var initializeTiming = _timing.MeasureStage("initialization");
                _controller = new SharedMapController(this, _settings, _players.Presence, _exploration, _portalRefresh!, _logger, _resources, _resourceRefresh, _players.RoutedMessages, _dungeons, _pinState, _runestones, _spawners, _structures, _timing) { HoldNewWork = _holdNewWork };
                _controller.Install();
            }
            _processing.Ready();
            if (now < _recoverUntil) ModuleScheduler.Current.ResumeGradually(_controller, _recoverUntil);
            _controller.Tick(now);
            SaveForProcessing(now);
        }
        catch (Exception error) { Fail(error); }
    }

    internal bool DeferPinDiagnostics(UnityEngine.Vector3 point) => _players.Arrivals.DeferPinComparison(point, UnityEngine.Time.unscaledTime);

    private void PresenceChanged(bool? occupied)
    {
        _occupied = occupied == true;
        if (!IsActive || _occupied) return;
        StopController();
        FlushStore();
        FlushPortalState();
        FlushResourceState();
        FlushDungeonState();
        if (occupied.HasValue) _processing.EmptyPaused();
        else _processing.Unavailable("player presence unavailable");
    }

    internal void Fail(Exception error)
    {
        if (!IsActive) return;
        ModuleFailureProtection.Record(Name, error, "controller/native callback or runtime processing"); DiagnosticLog.Write(_logger, Name, CoreLogTopics.Lifecycle, "Disabled after a runtime failure.", level: LogLevel.Error, error: error);
        Shutdown();
    }

    private bool ApplySwitchInPlace() => ApplySwitchInPlace(refreshPortals: false);

    private bool ApplySwitchInPlace(bool refreshPortals)
    {
        if (!IsActive || _callbackFailure != null) return false;
        _controller?.SwitchesChanged(refreshPortals);
        return true;
    }

    internal void FailAfterCallback(Exception error) => _callbackFailure ??= error;

    private void Log(string message, LogLevel level = LogLevel.Info) =>
        DiagnosticLog.Write(_logger, Name, message, level: level);

    private void StopController()
    {
        var controller = _controller; _controller = null; if (controller != null) ModuleScheduler.Current.Forget(controller);
        try { controller?.Dispose(); } finally { WorldObjectProvider.Current?.ReleaseOwner(Name); }
    }

    private void FlushStore()
    {
        if (_bootstrap == null && _store?.Save() == true) _diagnostics.Saved(UnityEngine.Time.unscaledTime, _exploration!.ExploredPixels);
    }

    private bool _holdSaveWarned;
    private void SaveForProcessing(float now)
    {
        try { SaveIfDue(now); }
        catch (Exception error) when (_holdNewWork && (error is IOException || error is UnauthorizedAccessException))
        {
            if (_holdSaveWarned) return;
            _holdSaveWarned = true;
            DiagnosticLog.Write(_logger, Name, CoreLogTopics.Control, "An admin change is waiting for SharedMap data that could not be saved.", level: LogLevel.Warning);
        }
    }

    private void SaveIfDue(float now)
    {
        float interval = _holdNewWork ? 0 : _settings.SaveIntervalSeconds.Value;
        using var snapshotTiming = _timing.MeasureStage("save_snapshots");
        if (_bootstrap == null && _store?.SaveIfDue(now, interval) == true) _diagnostics.Saved(now, _exploration!.ExploredPixels);
        if (_bootstrap == null && _portalRefresh?.SaveIfDue(now, interval) == true)
            _diagnostics.PortalStateSaved(now, _portalRefresh.Count);
        bool resourcesSaved = _resources?.SaveIfDue(now, interval) == true;
        bool publicationsSaved = _resourceRefresh?.SaveIfDue(now, interval) == true;
        if (resourcesSaved || publicationsSaved) _diagnostics.ResourceStateSaved(now, _resources?.Deposits.Count ?? 0);
        if (_dungeons?.SaveIfDue(now, interval) == true)
            _diagnostics.DungeonStateSaved(now, _dungeons.Count);
        _pinState?.SaveIfDue(now, interval);
        _runestones?.SaveIfDue(now, interval);
        _spawners?.SaveIfDue(now, interval);
        _structures?.SaveIfDue(now, interval);
    }

    private void FlushPortalState()
    {
        if (_bootstrap == null && _portalRefresh?.Save() == true)
            _diagnostics.PortalStateSaved(UnityEngine.Time.unscaledTime, _portalRefresh.Count);
    }

    private void FlushResourceState()
    {
        // Cleanup attempts both stores even if one fails.
        if (!ModuleCleanup.Run(_logger, Name, () => _resources?.Save(), () => _resourceRefresh?.Save()))
            throw new IOException("Resource state could not be flushed; prior files were retained.");
    }

    private void FlushDungeonState()
    {
        if (!ModuleCleanup.Run(_logger, Name,
            () => { if (_dungeons?.Save() == true) _diagnostics.DungeonStateSaved(UnityEngine.Time.unscaledTime, _dungeons.Count); },
            () => _pinState?.Save(), () => _runestones?.Save(), () => _spawners?.Save(), () => _structures?.Save())) throw new IOException("Dungeon/pin/landmark state could not be flushed.");
    }

    public void Shutdown()
    {
        ModuleScheduler.Current.Battle.Unregister(this);
        _recoverUntil = 0;
        IsActive = false;
        _callbackFailure = null;
        _occupied = false;
        _processing.Reset();
        var registration = _presenceRegistration;
        _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController, FlushStore, FlushPortalState, FlushResourceState, FlushDungeonState);
        _exploration = null; _store = null; _bootstrap = null; _portalRefresh = null; _world = null; _worldUid = 0; _resources = null; _resourceRefresh = null; _dungeons = null; _pinState = null;
        _runestones = null; _spawners = null; _structures = null;
    }
}
