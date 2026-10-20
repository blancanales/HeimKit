using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Features.SharedMap;
using HeimKit.Shared;
using HeimKit.Shared.Players;

internal static class SharedMapTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    public static void Run()
    {
        DiagnosticLog.StartSession();
        using var logger = new ManualLogSource("SharedMap checks");
        var messages = new List<string>();
        logger.LogEvent += (_, args) => messages.Add(args.Data.ToString()!);
        string directory = Path.Combine(AppContext.BaseDirectory, "shared-map-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "heimkit.cfg");
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var settings = new SharedMapSettings(config, logger);
            using var players = new PlayerServices(config, logger);
            var module = new SharedMap(config, logger, players);
            module.Initialize(GameVersionCompatibility.AuditedVersion);
            Check(settings.Enabled.Value && !settings.DebugMode.Value, "SharedMap must default enabled with debug off.");
            Check(settings.PortalClusterDistanceMeters.Value == 60 && settings.PortalMarkerSync.Value,
                "Automatic portals and default grouping were not configured independently.");
            Check(settings.ResourceMarkerSync.Value && !settings.ResourceDiscoverPreviouslyExplored.Value,
                "Resource sync must default on while retrospective discovery defaults off independently.");
            Check(settings.DungeonMarkerSync.Value && !settings.DungeonDiscoverPreviouslyExplored.Value && settings.AllowPlayerMapWrites.Value,
                "Dungeon sync and player writes must default on; retrospective dungeon discovery must default off.");
            Check(settings.PinDebugNameFilter.Value == "", "Pin debug filter must default to all labels.");
            Check(settings.SilverDiscoveryRadiusMeters.Value == 10, "Silver discovery must default to 10 metres.");
            Check(settings.ResourceClusterDistanceMeters.Value == 30 && settings.ResourceRemoveDepleted.Value,
                "Resource grouping/removal defaults did not match the reference configuration.");
            settings.Enabled.Value = false;
            module.Initialize(GameVersionCompatibility.AuditedVersion);
            Check(!module.IsActive && players.Events.Subscribers == 0, "Disabled module subscribed to presence.");
            Check(!config.ContainsKey(new ConfigDefinition("SharedMap", "DisableDebugLogging")), "Hidden suppression key was generated.");

            settings.Enabled.Value = true;
            module.Initialize("unsupported");
            Check(!module.IsActive && players.Events.Subscribers == 0, "Version mismatch activated SharedMap.");
            module.Initialize(GameVersionCompatibility.AuditedVersion);
            Check(module.IsActive && players.Events.Subscribers == 1, "Enabled module missed presence hookup. " + string.Join(" | ", messages));
            Check(players.Activity.Consumers == 0, "Exploration module acquired an activity lease.");
            Check(messages.Any(m => m.Contains("SharedMap: Enabled.")), "Startup omitted module enablement.");

            using var other = players.Events.Subscribe(_ => { }, error => throw error);
            module.Initialize(GameVersionCompatibility.AuditedVersion);
            Check(players.Events.Subscribers == 2, "Reinitialization leaked a subscription.");
            foreach (bool? occupied in new bool?[] { false, true, null, true, false })
            {
                players.Events.Observe(occupied);
                module.Tick(1f);
                Check(module.IsActive && players.Events.Subscribers == 2, "Presence transition disabled the module or lost another consumer.");
                Check(players.Activity.Consumers == 0, "Presence transition started idle inference.");
            }
            settings.Enabled.Value = false;
            module.Tick(2f);
            module.Shutdown();
            module.Shutdown();
            Check(!module.IsActive && players.Events.Subscribers == 1, "Shutdown leaked resources or removed another consumer.");

            File.WriteAllText(path, "[SharedMap]\nEnabled = true\nDebugMode = true\nDisableDebugLogging = true\n");
            var preserved = new ConfigFile(path, false) { SaveOnConfigSet = false };
            var reloaded = new SharedMapSettings(preserved, logger);
            Check(reloaded.Enabled.Value && reloaded.DebugMode.Value && !reloaded.DebugLogging.ConfiguredEnabled,
                "Existing settings or hidden suppression were ignored.");
            preserved.Save();
            Check(File.ReadAllText(path).Contains("DisableDebugLogging = true"), "Manual suppression was lost on save.");
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: SharedMap defaults, version guard, presence replay/transitions, repeatable lifecycle, consumer isolation and preserved hidden debug suppression.");
    }
}
