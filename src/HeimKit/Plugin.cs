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

namespace HeimKit;

[BepInPlugin("heimkit", "HeimKit", "0.2.0-rebuild.9")]
[BepInProcess("valheim_server.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private ModuleHost? _host;
    private PlayerServices? _players;
    private WorldObjectProvider? _world;

    private void Awake()
    {
        _host = new ModuleHost(Logger);
        DiagnosticLog.StartSession();
#if DEBUG
        DiagnosticLog.Write(Logger, "HeimKit", CoreLogTopics.Lifecycle, $"Starting version 0.2.0-rebuild.9 at {DiagnosticLog.WallClock().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}. Build configuration: Debug.");
#else
        DiagnosticLog.Write(Logger, "HeimKit", $"Starting version 0.2.0-rebuild.9 at {DiagnosticLog.WallClock().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}. Build configuration: Release.");
#endif
        try
        {
            ConfigurationLoader.Load(Paths.ConfigPath, Logger, config =>
            {
                _players = new PlayerServices(config, Logger);
                _world = new WorldObjectProvider(_players.Presence);
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
                return config;
            });
        }
        catch (Exception ex)
        {
            _players?.Dispose(); _players = null;
            _host.Dispose(); _world?.Dispose(); _world = null;
            DiagnosticLog.Write(Logger, "HeimKit", CoreLogTopics.Config, "Modules disabled because configuration setup failed.", level: LogLevel.Error, error: ex);
            return;
        }
        _host.Initialize(global::Version.CurrentVersion.ToString());
    }
    private void Update()
    {
        float now = Time.unscaledTime;
        WorldServices.Refresh();
        _players?.Tick(now);
        _world?.Tick(now);
        _host?.Tick(now);
        _players?.Events.ReportProcessing();
    }
    private void OnDestroy()
    {
        _host?.Dispose(); _host = null;
        _world?.Dispose(); _world = null;
        _players?.Dispose(); _players = null;
        PrefabRegistry.Current.Clear();
    }
}
