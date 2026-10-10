using System;
using System.Collections.Generic;
using BepInEx;
using HeimKit.Configuration;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Shared;
using UnityEngine;

namespace HeimKit;

[BepInPlugin("heimkit", "HeimKit", "0.1.1")]
[BepInProcess("valheim_server.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private readonly List<IServerModule> _features = new();

    private void Awake()
    {
        Logger.LogInfo("HeimKit 0.1.1 starting.");
        InfiniteTorches torches;
        try
        {
            torches = ConfigurationLoader.Load(Paths.ConfigPath, Logger,
                config => new InfiniteTorches(config, Logger));
        }
        catch (Exception ex)
        {
            Logger.LogError($"Infinite Torches: DISABLED because configuration setup failed: {ex.Message}");
            return;
        }
        Logger.LogInfo("To enable or disable Infinite Torches: stop the server, edit [Torches] Enabled = true or false in heimkit.cfg, save, then restart the server.");
        _features.Add(torches);
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
