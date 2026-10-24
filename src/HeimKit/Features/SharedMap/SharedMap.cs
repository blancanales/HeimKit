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

public sealed class SharedMap : IServerModule
{
    private readonly SharedMapSettings _settings;
    private readonly ManualLogSource _logger;
    private readonly GameVersionCompatibility _compatibility;
    private readonly PlayerServices _players;
    private readonly PlayerProcessingNotices _processing;
    private IDisposable? _presenceRegistration;
    private bool _occupied;
    private SharedMapController? _controller;
    private SharedMapExploration? _exploration;
    private ZDOMan? _world;
    private long _worldUid;
    private readonly string _storeDirectory;
    private readonly SharedMapDiagnostics _diagnostics;
    private SharedMapStore? _store;
    private SharedMapBootstrap? _bootstrap;
    private SharedMapMarkerRefresh? _portalRefresh;
    private SharedMapResourceStore? _resources;
    private SharedMapMarkerRefresh? _resourceRefresh;
    private SharedMapDungeonStore? _dungeons;
    private SharedMapPinState? _pinState;
    private SharedMapLandmarkStore? _runestones, _spawners;

    internal string StoreDirectory => _storeDirectory;
    public string Name => "SharedMap";
    public bool IsActive { get; private set; }

    public SharedMap(ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        _settings = new SharedMapSettings(config, logger);
        _logger = logger;
        _compatibility = GameVersionCompatibility.Resolve(config, logger);
        _players = players;
        _storeDirectory = Path.Combine(Path.GetDirectoryName(config.ConfigFilePath)!, "HeimKit", "SharedMap");
        _diagnostics = new SharedMapDiagnostics(logger, _settings.DebugLogging);
        _processing = new PlayerProcessingNotices(logger, Name, () => _settings.DebugLogging.Enabled);
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        Log(_settings.Enabled.Value ? "Enabled." : "Disabled.");
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
            SharedMapPatches.CheckCompatibility();
            if (_settings.ResourceMarkerSync.Value) SharedMapResources.CheckCompatibility();
            if (_settings.DungeonMarkerSync.Value) SharedMapDungeons.CheckCompatibility();
            if (_settings.RunestoneMarkerSync.Value || _settings.SpawnerMarkerSync.Value) SharedMapLandmarks.CheckCompatibility();
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
            SaveIfDue(now);
            bool ready = PlayerServices.Ready && ZoneSystem.instance != null && ZRoutedRpc.instance != null && ZNetScene.instance != null && Minimap.instance != null;
            if (ready) _processing.ServerReady();
            if (!ready) { StopController(); if (_occupied) _processing.Paused("world or minimap services unavailable"); return; }
            var minimap = Minimap.instance;
            if (minimap == null) { StopController(); return; }
            long uid = ZNet.instance.GetWorldUID();
            if (!ReferenceEquals(_world, ZDOMan.instance) || _worldUid != uid || _exploration == null ||
                !_exploration.Matches(minimap.m_textureSize, minimap.m_pixelSize, minimap.m_exploreRadius, minimap.m_exploreInterval))
            {
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
                _runestones?.Load(); _spawners?.Load();
                _pinState = new SharedMapPinState(_storeDirectory, uid, _diagnostics.PinStateChanged);
                _pinState.Load();
                _exploration = exploration; _store = store;
                _portalRefresh = portalRefresh;
                _bootstrap = loaded ? null : new SharedMapBootstrap(ZDOMan.instance, exploration);
                _world = ZDOMan.instance; _worldUid = uid;
                _diagnostics.StoreOpened(loaded, uid, store.FilePath, exploration.ExploredPixels);
                _diagnostics.PortalStateOpened(loadedPortalState, portalRefresh.FilePath, portalRefresh.Count);
            }
            if (_bootstrap != null)
            {
                if (!_bootstrap.Tick(_settings.MaxTablesPerPass.Value)) return;
                // Commit even an empty import. Until this succeeds, lifecycle flushes must not save partial work.
                _store!.Save(initialize: true);
                var completed = _bootstrap;
                _bootstrap = null;
                _diagnostics.Bootstrapped(completed.Tables, completed.Imported, completed.Invalid, _exploration!.ExploredPixels);
            }
            if (!_occupied) return;
            if (_controller == null)
            {
                _controller = new SharedMapController(this, _settings, _players.Presence, _exploration, _portalRefresh!, _logger, _resources, _resourceRefresh, _players.RoutedMessages, _dungeons, _pinState, _runestones, _spawners);
                _controller.Install();
            }
            _processing.Ready();
            _controller.Tick(now);
            SaveIfDue(now);
        }
        catch (Exception error) { Fail(error); }
    }

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
        DiagnosticLog.Write(_logger, Name, CoreLogTopics.Lifecycle, "Disabled after a runtime failure.", level: LogLevel.Error, error: error);
        Shutdown();
    }

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

    private void SaveIfDue(float now)
    {
        if (_bootstrap == null && _store?.SaveIfDue(now, _settings.SaveIntervalSeconds.Value) == true) _diagnostics.Saved(now, _exploration!.ExploredPixels);
        if (_bootstrap == null && _portalRefresh?.SaveIfDue(now, _settings.SaveIntervalSeconds.Value) == true)
            _diagnostics.PortalStateSaved(now, _portalRefresh.Count);
        bool resourcesSaved = _resources?.SaveIfDue(now, _settings.SaveIntervalSeconds.Value) == true;
        bool publicationsSaved = _resourceRefresh?.SaveIfDue(now, _settings.SaveIntervalSeconds.Value) == true;
        if (resourcesSaved || publicationsSaved) _diagnostics.ResourceStateSaved(now, _resources?.Deposits.Count ?? 0);
        if (_dungeons?.SaveIfDue(now, _settings.SaveIntervalSeconds.Value) == true)
            _diagnostics.DungeonStateSaved(now, _dungeons.Count);
        _pinState?.SaveIfDue(now, _settings.SaveIntervalSeconds.Value);
        _runestones?.SaveIfDue(now, _settings.SaveIntervalSeconds.Value);
        _spawners?.SaveIfDue(now, _settings.SaveIntervalSeconds.Value);
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
            () => _pinState?.Save(), () => _runestones?.Save(), () => _spawners?.Save())) throw new IOException("Dungeon/pin/landmark state could not be flushed.");
    }

    public void Shutdown()
    {
        IsActive = false;
        _occupied = false;
        _processing.Reset();
        var registration = _presenceRegistration;
        _presenceRegistration = null;
        ModuleCleanup.Run(_logger, Name, () => registration?.Dispose(), StopController, FlushStore, FlushPortalState, FlushResourceState, FlushDungeonState);
        _exploration = null; _store = null; _bootstrap = null; _portalRefresh = null; _world = null; _worldUid = 0; _resources = null; _resourceRefresh = null; _dungeons = null; _pinState = null;
        _runestones = null; _spawners = null;
    }
}
