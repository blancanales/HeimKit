using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Core.Control;
using HeimKit.Core.Control.Models;
using HeimKit.Core.Control.Server;
using HeimKit.Configuration;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;

internal static class ModuleSwitchesTests
{
    public static void Run()
    {
        using var log = new ManualLogSource("Switch catalog");
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
        using var players = new PlayerServices(config, log);
        var diagnostics = new ServerDiagnosticsControls(config);
        if (diagnostics.Set("DebugVerbosity", true, "Local host", log) != SetResult.Applied ||
            DiagnosticsSettings.Verbosity != DebugVerbosity.Verbose ||
            diagnostics.Set("Enabled", true, "Local host", log) != SetResult.UnknownSwitch)
            throw new Exception("Live verbosity update or diagnostic key validation failed.");
        var shared = new List<SwitchState>();
        diagnostics.AddSnapshot(shared);
        if (shared.Count != 3 || shared.Any(s => s.Definition.IsModuleSwitch) ||
            !shared.Single(s => s.Definition.Key == "DebugVerbosity").Value ||
            shared.Single(s => s.Definition.Key == "DebugMode").Value ||
            !File.ReadAllText(config.ConfigFilePath).Contains("DebugVerbosity = Verbose"))
            throw new Exception("Verbose selection must persist and leave global debugging off.");
        diagnostics.Set("DebugVerbosity", false, "Local host", log);
        var modules = new (IServerModule Module, string Keys)[]
        {
            (new HeimKit.Features.InfiniteTorches.InfiniteTorches(config, log, players), "Enabled DebugMode IncludeFireplaces Campfires Hearths Bonfires"),
            (new HeimKit.Features.WeatherGuard.WeatherGuard(config, log, players), "Enabled DebugMode ProtectRain ProtectUnderwater"),
            (new HeimKit.Features.AFKNightWatch.AFKNightWatch(config, log, players), "Enabled DebugMode MajoritySleepEnabled ExcludeAFKFromSleep RequireMoreThanPercent"),
            (new HeimKit.Features.AFKRaidWatch.AFKRaidWatch(config, log, players), "Enabled DebugMode"),
            (new HeimKit.Features.SharedMap.SharedMap(config, log, players), "Enabled DebugMode PortalMarkerSync ResourceMarkerSync ResourceDiscoverPreviouslyExplored ResourceRemoveDepleted DungeonMarkerSync DungeonDiscoverPreviouslyExplored AllowPlayerMapWrites SharePinCheckmarks DungeonLootProgress RunestoneMarkerSync SpawnerMarkerSync DvergrGuardTowerMarkerSync DvergrLighthouseMarkerSync DvergrExcavationMarkerSync FulingVillageMarkerSync OtherStructureMarkerSync"),
            (new HeimKit.Features.DoorKeeper.DoorKeeper(config, log, players), "Enabled DebugMode"),
            (new HeimKit.Features.ForestRenewal.ForestRenewal(config, log, players), "Enabled DebugMode ExcludeClearedGround"),
            (new HeimKit.Features.SeaSalvage.SeaSalvage(config, log, players), "Enabled DebugMode"),
            (new HeimKit.Features.TameTravel.TameTravel(config, log, players), "Enabled DebugMode SummonTravel FollowSpacingEnabled"),
            (new HeimKit.Features.HeimMender.HeimMender(config, log, players), "Enabled DebugMode AutomaticEnabled"),
            (new HeimKit.Features.StationSupply.StationSupply(config, log, players), "Enabled DebugMode Smelters CharcoalKilns BlastFurnaces Windmills SpinningWheels EitrRefineries FrigidKilns ShieldGenerators Fermenters Obliterators"),
            (new HeimKit.Features.HeimDefender.HeimDefender(config, log, players), "Enabled FreeWoodBolts DebugMode"),
        };
        if (!modules.Select(m => m.Module.Name).SequenceEqual(ControlProtocol.ModuleNames))
            throw new Exception("Client-only module list differs from the server order.");
        foreach (var (module, keys) in modules)
        {
            var settings = (IModuleSwitches)module;
            if (!settings.Switches.Select(s => s.Entry.Definition.Key).SequenceEqual(keys.Split(' ')))
                throw new Exception("Switch catalog mismatch for " + module.Name);
            if (settings.Switches.Any(s => s.ApplyLive != (s.Entry.Definition.Key == "DebugMode")))
                throw new Exception("Live debug binding mismatch for " + module.Name);
            var inPlaceKeys = module.Name switch
            {
                "WeatherGuard" => new[] { "ProtectRain", "ProtectUnderwater" },
                "SharedMap" => new[] { "PortalMarkerSync", "ResourceDiscoverPreviouslyExplored", "ResourceRemoveDepleted",
                    "DungeonDiscoverPreviouslyExplored", "AllowPlayerMapWrites", "SharePinCheckmarks" },
                _ => Array.Empty<string>(),
            };
            if (!settings.Switches.Where(s => s.ApplyInPlace != null).Select(s => s.Entry.Definition.Key).SequenceEqual(inPlaceKeys))
                throw new Exception("In-place switch binding mismatch for " + module.Name);
            if (settings.ApplyMode != (module.Name is "HeimMender" or "HeimDefender" ? SwitchApplyMode.SelfReconciling : SwitchApplyMode.Reinitialize))
                throw new Exception("Switch apply mode mismatch for " + module.Name);
        }
    }
}
