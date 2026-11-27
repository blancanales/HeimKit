using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Core.Players;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Features.HeimDefender;
using HeimKit.Features.WeatherGuard;
using HeimKit.Features.AFKNightWatch;
using HeimKit.Features.AFKRaidWatch;
using HeimKit.Features.SharedMap;
using HeimKit.Features.DoorKeeper;
using HeimKit.Features.ForestRenewal;
using HeimKit.Features.SeaSalvage;
using HeimKit.Features.TameTravel;
using HeimKit.Features.HeimMender;
using HeimKit.Features.StationSupply;

namespace HeimKit.Core.Scheduling;

internal static class GameplayModules
{
    internal static void Register(ModuleHost host, ConfigFile config, ManualLogSource logger, PlayerServices players)
    {
        host.Register(() => new InfiniteTorches(config, logger, players), "InfiniteTorches");
        host.Register(() => new WeatherGuard(config, logger, players), "WeatherGuard");
        host.Register(() => new AFKNightWatch(config, logger, players), "AFKNightWatch");
        host.Register(() => new AFKRaidWatch(config, logger, players), "AFKRaidWatch");
        host.Register(() => new SharedMap(config, logger, players), "SharedMap");
        host.Register(() => new DoorKeeper(config, logger, players), "DoorKeeper");
        host.Register(() => new ForestRenewal(config, logger, players), "ForestRenewal");
        host.Register(() => new SeaSalvage(config, logger, players), "SeaSalvage");
        host.Register(() => new TameTravel(config, logger, players), "TameTravel");
        host.Register(() => new HeimMender(config, logger, players), "HeimMender");
        host.Register(() => new StationSupply(config, logger, players), "StationSupply");
        host.Register(() => new HeimDefender(config, logger, players), "HeimDefender");
    }
}
