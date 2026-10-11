using System;
using System.Collections.Generic;
using BepInEx;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.WeatherGuard;
using HeimKit.Shared;
using UnityEngine;

namespace HeimKit;

[BepInPlugin("heimkit", "HeimKit", "0.1.12")]
[BepInProcess("valheim_server.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private readonly List<IServerModule> _features = new();

    private void Awake()
    {
        Logger.LogInfo("HeimKit 0.1.12 starting.");
        try
        {
            ConfigurationLoader.Load(Paths.ConfigPath, Logger, config =>
            {
                Register(() => new InfiniteTorches(config, Logger), "Infinite Torches");
                Register(() => new WeatherGuard(config, Logger), "WeatherGuard");
                return config;
            });
        }
        catch (Exception ex)
        {
            _features.Clear();
            Logger.LogError($"HeimKit: modules DISABLED because configuration setup failed: {ex.Message}");
            return;
        }
        Logger.LogInfo("To enable or disable Infinite Torches: stop the server, edit [Torches] Enabled = true or false in heimkit.cfg, save, then restart the server.");
        foreach (var feature in _features)
        {
            try
            {
                feature.Initialize(global::Version.CurrentVersion.ToString());
            }
            catch (Exception ex)
            {
                feature.Shutdown();
                Logger.LogError($"{feature.Name}: DISABLED after startup error: {ex}");
            }
        }
    }

    private void Register(Func<IServerModule> create, string name)
    {
        try { _features.Add(create()); }
        catch (Exception error) { Logger.LogError($"{name}: DISABLED after configuration construction error: {error}"); }
    }

    private void Update()
    {
        foreach (var feature in _features)
        {
            if (!feature.IsActive) continue;
            try
            {
                feature.Tick(Time.unscaledTime);
            }
            catch (Exception ex)
            {
                feature.Shutdown();
                Logger.LogError($"{feature.Name}: DISABLED after runtime error: {ex}");
            }
        }
    }

    private void OnDestroy()
    {
        foreach (var feature in _features)
        {
            try { feature.Shutdown(); }
            catch (Exception ex) { Logger.LogError($"{feature.Name}: shutdown error: {ex}"); }
        }
        _features.Clear();
    }
}
