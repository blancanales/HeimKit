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
using HeimKit.Client;
using HeimKit.Core.Control.Server;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.WeatherGuard;
using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit;

[BepInPlugin("heimkit", "HeimKit", "0.2.4")]
[BepInProcess("valheim_server.exe")]
[BepInProcess("valheim.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private ProcessRole _role;
    private ClientHost? _client;
    private ControlServer? _control;
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
        _role = string.Equals(Paths.ProcessName, "valheim", StringComparison.OrdinalIgnoreCase)
            ? ProcessRole.GameClient : ProcessRole.DedicatedServer;
        if (_role == ProcessRole.GameClient)
        {
            try { _client = new ClientHost(gameObject, Paths.ConfigPath, Info.Metadata.Version.ToString() + Info.Metadata.VersionExtra, Logger); }
            catch (Exception error) { DiagnosticLog.Write(Logger, "HeimKit", CoreLogTopics.Control, "HeimKit Settings could not start.", level: LogLevel.Error, error: error); }
            return;
        }
        _host = new ModuleHost(Logger);
        DiagnosticLog.StartSession();
#if DEBUG
        DiagnosticLog.Write(Logger, "HeimKit", CoreLogTopics.Lifecycle, $"Starting version 0.2.4 at {DiagnosticLog.WallClock().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}. Build configuration: Debug.");
#else
        DiagnosticLog.Write(Logger, "HeimKit", $"Starting version 0.2.4 at {DiagnosticLog.WallClock().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}. Build configuration: Release.");
#endif
        try
        {
            _failureProtection = new ModuleFailureProtection(Logger);
            ControlSettings? controlSettings = null;
            var configuration = ConfigurationLoader.Load(Paths.ConfigPath, Logger, config =>
            {
                GameVersionCompatibility.Resolve(config, Logger);
                controlSettings = new ControlSettings(config);
                _players = new PlayerServices(config, Logger);
                _host.AttachPresence(_players.Events);
                _world = new WorldObjectProvider(_players.Presence, logger: Logger);
                GameplayModules.Register(_host, config, Logger, _players);
                return config;
            });
            _gameVersion = global::Version.CurrentVersion.ToString();
            var policy = GameVersionCompatibility.Resolve(configuration, Logger);
            policy.Supports(_gameVersion);
            _startup = new UpgradeStartup(policy, _gameVersion, Paths.ConfigPath, Logger);
            if (controlSettings != null)
            {
                try
                {
                    _control = new ControlServer(controlSettings, _host, _players!, Logger, Info.Metadata.Version.ToString() + Info.Metadata.VersionExtra, policy, _gameVersion);
                    _control.InstallIdentification();
                }
                catch (Exception error)
                {
                    var control = _control; _control = null;
                    ModuleCleanup.Run(Logger, "HeimKit", () => control?.Dispose());
                    DiagnosticLog.Write(Logger, "HeimKit", CoreLogTopics.Control, "HeimKit Settings could not start.", level: LogLevel.Error, error: error);
                }
            }
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
        if (_role == ProcessRole.GameClient)
        {
            _client?.Tick(Time.unscaledTime);
            return;
        }
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
            if (_players != null) _players.Connections.ClientDetectionAvailable = _control?.DetectsClients == true;
            using (PerformanceStages.Measure("HeimKit", "player_services")) _players?.Tick(now);
            _failureProtection?.ThrowIfFailed();
            using (PerformanceStages.Measure("HeimKit", "world_preparation")) _world?.Tick(now);
            _failureProtection?.ThrowIfFailed();
            using (PerformanceStages.Measure("HeimKit", "feature_dispatch")) _host?.Tick(now, _players?.Presence.HasJoinedPlayers, beginFrame: false);
            _failureProtection?.ThrowIfFailed();
            _control?.Tick(now);
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
        if (_role == ProcessRole.GameClient)
        {
            _client?.Dispose();
            _client = null;
            return;
        }
        var control = _control; _control = null;
        ModuleCleanup.Run(Logger, "HeimKit", () => control?.Dispose());
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
