using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Core.Players;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.SharedMap;
using UnityEngine;
using HeimKit.Features.DoorKeeper;
using HeimKit.Features.ForestRenewal;
using HeimKit.Features.SeaSalvage;
using HeimKit.Features.TameTravel;
using HeimKit.Features.HeimMender;
using HeimKit.Features.StationSupply;

namespace HeimKit;

[BepInPlugin("heimkit", "HeimKit", "0.2.3")]
[BepInProcess("valheim_server.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private ModuleHost? _host;
    private PlayerServices? _players;
    private WorldObjectProvider? _world;
    private UpgradeStartup? _startup;
    private ModuleFailureProtection? _failureProtection;
    private ServerSaveRequests? _saveRequests;
    private string _gameVersion = "";
    private bool _modulesInitialized, _processingBlocked;

    private void Awake()
    {
        _host = new ModuleHost(Logger);
        DiagnosticLog.StartSession();
#if DEBUG
        DiagnosticLog.Write(Logger, "HeimKit", CoreLogTopics.Lifecycle, $"Starting version 0.2.3 at {DiagnosticLog.WallClock().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}. Build configuration: Debug.");
#else
        DiagnosticLog.Write(Logger, "HeimKit", $"Starting version 0.2.3 at {DiagnosticLog.WallClock().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}. Build configuration: Release.");
#endif
        try
        {
            _failureProtection = new ModuleFailureProtection(Logger);
            var configuration = ConfigurationLoader.Load(Paths.ConfigPath, Logger, config =>
            {
                GameVersionCompatibility.Resolve(config, Logger);
                _players = new PlayerServices(config, Logger);
                _world = new WorldObjectProvider(_players.Presence, logger: Logger);
                _host.Register(() => new InfiniteTorches(config, Logger, _players), "InfiniteTorches");
                _host.Register(() => new WeatherGuard(config, Logger, _players), "WeatherGuard");
                _host.Register(() => new AFKNightWatch(config, Logger, _players), "AFKNightWatch");
                _host.Register(() => new AFKRaidWatch(config, Logger, _players), "AFKRaidWatch");
                _host.Register(() => new SharedMap(config, Logger, _players), "SharedMap");
                _host.Register(() => new DoorKeeper(config, Logger, _players), "DoorKeeper");
                _host.Register(() => new ForestRenewal(config, Logger, _players), "ForestRenewal");
                _host.Register(() => new SeaSalvage(config, Logger, _players), "SeaSalvage");
                _host.Register(() => new TameTravel(config, Logger, _players), "TameTravel");
                _host.Register(() => new HeimMender(config, Logger, _players), "HeimMender");
                _host.Register(() => new StationSupply(config, Logger, _players), "StationSupply");
                return config;
            });
            _gameVersion = global::Version.CurrentVersion.ToString();
            var policy = GameVersionCompatibility.Resolve(configuration, Logger);
            policy.Supports(_gameVersion);
            _startup = new UpgradeStartup(policy, _gameVersion, Paths.ConfigPath, Logger);
            _startup.Initialize();
            InitializeModulesIfReady();
        }
        catch (Exception ex)
        {
            StopProcessing("Modules disabled because shared startup setup failed.", ex);
            return;
        }
    }
    private bool InitializeModulesIfReady()
    {
        if (_modulesInitialized) return true;
        if (_startup?.Ready != true) return false;
        if (!_startup.PrepareFailureProtection(_failureProtection!)) return false;
        _startup.Dispose();
        if (_gameVersion == GameVersionCompatibility.AuditedVersion)
        {
            try { _saveRequests = new ServerSaveRequests(Logger); }
            catch (Exception error) { DiagnosticLog.Write(Logger, "HeimKit", "Native save-request compatibility and observation hooks are unavailable: " + error.GetType().Name + ". Native saving remains unchanged.", LogLevel.Warning); }
        }
        _host!.Initialize(_gameVersion);
        _failureProtection!.ThrowIfFailed();
        _modulesInitialized = true;
        return true;
    }
    private void Update()
    {
        if (_processingBlocked) return;
        try
        {
            _failureProtection?.ThrowIfFailed();
            if (!InitializeModulesIfReady()) return;
            if (_startup?.PrepareFailureProtection(_failureProtection!) != true)
                throw new InvalidOperationException("The protected dedicated-server world is no longer available.");
            float now = Time.unscaledTime;
            long measuredUpdate = ServerPerformance.BeginUpdate();
            ModuleScheduler.Current.BeginFrame(now);
            using (PerformanceStages.Measure("HeimKit", "world_services")) WorldServices.Refresh();
            using (PerformanceStages.Measure("HeimKit", "player_services")) _players?.Tick(now);
            _failureProtection?.ThrowIfFailed();
            using (PerformanceStages.Measure("HeimKit", "world_preparation")) _world?.Tick(now);
            _failureProtection?.ThrowIfFailed();
            using (PerformanceStages.Measure("HeimKit", "feature_dispatch")) _host?.Tick(now, _players?.Presence.HasJoinedPlayers, beginFrame: false);
            _failureProtection?.ThrowIfFailed();
            using (PerformanceStages.Measure("HeimKit", "player_reporting")) _players?.Events.ReportProcessing();
            using (PerformanceStages.Measure("HeimKit", "player_diagnostics")) _players?.Context.Tick(now);
            using (PerformanceStages.Measure("PlayerActivity", "arrival_diagnostics")) _players?.Arrivals.EndFrame(now, measuredUpdate);
            ServerPerformance.EndUpdate(measuredUpdate, now, Logger, _players?.Presence.JoinedCount,
                _world?.FrameRecords ?? 0, ZNetScene.instance == null ? 0 : ZNetScene.instance.NrOfInstances(), WeatherGuardController.Active?.RegionCount ?? 0);
        }
        catch (Exception error) { StopProcessing("Modules disabled after a shared processing failure.", error); }
    }
    private void StopProcessing(string reason, Exception error)
    {
        _processingBlocked = true;
        OnDestroy();
        DiagnosticLog.Write(Logger, "HeimKit", CoreLogTopics.Lifecycle, reason, level: LogLevel.Error, error: error);
    }
    private void OnDestroy()
    {
        _processingBlocked = true;
        var startup = _startup; _startup = null;
        var host = _host; _host = null;
        var world = _world; _world = null;
        var players = _players; _players = null;
        var protection = _failureProtection; _failureProtection = null;
        var saves = _saveRequests; _saveRequests = null;
        ModuleCleanup.Run(Logger, "HeimKit", () => saves?.Dispose());
        ModuleCleanup.Run(Logger, "HeimKit", () => startup?.Dispose());
        ModuleCleanup.Run(Logger, "HeimKit", () => host?.Dispose());
        ModuleCleanup.Run(Logger, "HeimKit", () => world?.Dispose());
        ModuleCleanup.Run(Logger, "HeimKit", () => players?.Dispose());
        ModuleCleanup.Run(Logger, "HeimKit", () => protection?.Dispose());
        PrefabRegistry.Current.Clear();
    }
}
