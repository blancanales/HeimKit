using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.MajoritySleep;
using HeimKit.Features.AfkSleepExclusion;
using HeimKit.Features.AfkRaidProtection;
using HeimKit.Shared;
using UnityEngine;

namespace HeimKit;

[BepInPlugin("heimkit", "HeimKit", "0.1.20")]
[BepInProcess("valheim_server.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private readonly List<IServerModule> _features = new();

    private void Awake()
    {
        DiagnosticLog.StartSession();
        DiagnosticLog.Write(Logger, "HeimKit", DiagnosticEvent.Lifecycle, "Starting", new (string, object?)[] { ("version", "0.1.20"), ("log_schema", 1) });
        try
        {
            ConfigurationLoader.Load(Paths.ConfigPath, Logger, config =>
            {
                Register(() => new InfiniteTorches(config, Logger), "Infinite Torches");
                Register(() => new WeatherGuard(config, Logger), "WeatherGuard");
                Register(() => new MajoritySleep(config, Logger), "MajoritySleep");
                Register(() => new AfkSleepExclusion(config, Logger), "AfkSleepExclusion");
                Register(() => new AfkRaidProtection(config, Logger), "AfkRaidProtection");
                return config;
            });
        }
        catch (Exception ex)
        {
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
                feature.Shutdown();
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
                feature.Shutdown();
                DiagnosticLog.Write(Logger, feature.Name, DiagnosticEvent.Lifecycle, $"DISABLED after runtime error: {ex}", level: LogLevel.Error);
            }
        }
    }

    private void OnDestroy()
    {
        foreach (var feature in _features)
        {
            try { feature.Shutdown(); }
            catch (Exception ex) { DiagnosticLog.Write(Logger, feature.Name, DiagnosticEvent.Lifecycle, $"shutdown error: {ex}", level: LogLevel.Error); }
        }
        _features.Clear();
    }
}
