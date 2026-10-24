namespace HeimKit.Core.World;

internal static class WorldServices
{
    internal static bool Network { get; private set; }
    internal static bool Activity { get; private set; }
    internal static bool World { get; private set; }
    internal static bool Terrain { get; private set; }
    internal static void Refresh()
    {
        var net = ZNet.instance;
        Network = net != null && net.IsServer() && net.IsDedicated() && ZDOMan.instance != null;
        Activity = Network && Game.instance != null;
        World = Network && ZNetScene.instance != null && ZRoutedRpc.instance != null;
        Terrain = World && ZoneSystem.instance != null;
    }
}
