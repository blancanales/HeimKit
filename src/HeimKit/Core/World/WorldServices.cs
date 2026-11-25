namespace HeimKit.Core.World;

internal static class WorldServices
{
    internal static bool Network { get; private set; }
    internal static bool Activity { get; private set; }
    internal static bool World { get; private set; }
    internal static bool Terrain { get; private set; }
    internal static void Refresh(bool playerHosted = false)
    {
        var net = ZNet.instance;
        var role = WorldSession.Identify(net);
        Network = (playerHosted ? WorldSession.IsPlayerHosted(role) : role == WorldSessionRole.DedicatedServer) &&
            ZDOMan.instance != null;
        Activity = Network && Game.instance != null;
        World = Network && ZNetScene.instance != null && ZRoutedRpc.instance != null;
        Terrain = World && ZoneSystem.instance != null;
    }
}
