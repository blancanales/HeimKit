using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Shared;
using HeimKit.Shared.Players;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.SharedMap;
using UnityEngine;

namespace HeimKit;

[BepInPlugin("heimkit", "HeimKit", "0.1.55")]
[BepInProcess("valheim_server.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private readonly List<IServerModule> _features = new();

    private PlayerServices? _players;

    private void Awake()
    {
        DiagnosticLog.StartSession();
#if DEBUG
        DiagnosticLog.Write(Logger, "HeimKit", DiagnosticEvent.Lifecycle, "Starting version 0.1.55. Build configuration: Debug.");
#else
        DiagnosticLog.Write(Logger, "HeimKit", DiagnosticEvent.Lifecycle, "Starting version 0.1.55. Build configuration: Release.");
#endif
        try
        {
            ConfigurationLoader.Load(Paths.ConfigPath, Logger, config =>
            {
                _players = new PlayerServices(config, Logger);
                Register(() => new InfiniteTorches(config, Logger, _players), "InfiniteTorches");
                Register(() => new WeatherGuard(config, Logger, _players), "WeatherGuard");
                Register(() => new AFKNightWatch(config, Logger, _players), "AFKNightWatch");
                Register(() => new AFKRaidWatch(config, Logger, _players), "AFKRaidWatch");
                Register(() => new SharedMap(config, Logger, _players), "SharedMap");
                return config;
            });
        }
        catch (Exception ex)
        {
            _players?.Dispose(); _players = null;
            _features.Clear();
            DiagnosticLog.Write(Logger, "HeimKit", DiagnosticEvent.Lifecycle, $"modules DISABLED because configuration setup failed: {ex.Message}", level: LogLevel.Error);
            return;
        }
        foreach (var feature in _features)
        {
            try
            {
                feature.Initialize(global::Version.CurrentVersion.ToString());
            }
            catch (Exception ex)
            {
                ModuleCleanup.Run(Logger, feature.Name, feature.Shutdown);
                DiagnosticLog.Write(Logger, feature.Name, DiagnosticEvent.Lifecycle, $"DISABLED after startup error: {ex}", level: LogLevel.Error);
            }
        }
    }

    private void Register(Func<IServerModule> create, string name)
    {
        try { _features.Add(create()); }
        catch (Exception error) { DiagnosticLog.Write(Logger, name, DiagnosticEvent.Lifecycle, $"DISABLED after configuration construction error: {error}", level: LogLevel.Error); }
    }

    private void Update()
    {
        _players?.Tick(Time.unscaledTime);
        foreach (var feature in _features)
        {
            // Tick inactive modules too so explicit operator requests remain available.
            // Each module gates its own gameplay processing on IsActive.
            try
            {
                feature.Tick(Time.unscaledTime);
            }
            catch (Exception ex)
            {
                ModuleCleanup.Run(Logger, feature.Name, feature.Shutdown);
                DiagnosticLog.Write(Logger, feature.Name, DiagnosticEvent.Lifecycle, $"DISABLED after runtime error: {ex}", level: LogLevel.Error);
            }
        }
        _players?.Events.ReportProcessing();
    }

    private void OnDestroy()
    {
        foreach (var feature in _features)
        {
            ModuleCleanup.Run(Logger, feature.Name, feature.Shutdown);
        }
        _features.Clear();
        _players?.Dispose(); _players = null;
    }
}
